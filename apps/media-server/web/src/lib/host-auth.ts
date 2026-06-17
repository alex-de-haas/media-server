import type { NextRequest } from "next/server";
import { hostyServerEnv } from "@/lib/hosty";
import { type AppRole, identityCookieAttributes, mapHostRole } from "@/lib/identity";

/** App-origin HttpOnly cookie holding the Host identity token. */
export const IDENTITY_COOKIE = "hosty_identity";

export interface HostSession {
  userId: string;
  email: string | null;
  displayName: string | null;
  role: AppRole;
}

/**
 * Reads the identity token from (in priority order) the Authorization bearer header — the
 * in-memory/sessionStorage fallback the browser sends when the cross-site cookie is blocked —
 * then the app-origin cookie.
 */
export function readIdentityToken(request: NextRequest): string | null {
  const authorization = request.headers.get("authorization");
  if (authorization && authorization.toLowerCase().startsWith("bearer ")) {
    const token = authorization.slice("bearer ".length).trim();
    if (token.length > 0) {
      return token;
    }
  }

  const cookie = request.cookies.get(IDENTITY_COOKIE)?.value;
  return cookie && cookie.length > 0 ? cookie : null;
}

/** True when the effective request protocol is https (honouring the ingress forwarded proto). */
export function isSecureRequest(request: NextRequest): boolean {
  const forwarded = request.headers.get("x-forwarded-proto");
  if (forwarded) {
    return forwarded.split(",")[0].trim().toLowerCase() === "https";
  }
  return request.nextUrl.protocol === "https:";
}

/**
 * Cookie attributes derived from the effective protocol. The app is embedded in a cross-site
 * Shell iframe, so https needs `SameSite=None; Secure`; plain http (local dev) cannot use
 * `Secure` and falls back to `SameSite=Lax`.
 */
export function identityCookieOptions(request: NextRequest, maxAgeSeconds: number) {
  return identityCookieAttributes(isSecureRequest(request), maxAgeSeconds);
}

interface RevalidateResponse {
  active: boolean;
  appId: string;
  userId: string;
  email?: string;
  displayName?: string;
  hostRole?: string;
}

/**
 * Revalidates a forwarded identity token against Core with the app service token. Core identity
 * tokens are HS256 (symmetric), so this round-trip is the only trustworthy validation.
 */
export async function revalidateIdentity(token: string): Promise<HostSession | null> {
  const env = hostyServerEnv();
  if (!env.serviceToken) {
    return null;
  }

  let response: Response;
  try {
    response = await fetch(`${env.coreOrigin}/api/auth/apps/revalidate`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: `Bearer ${env.serviceToken}`,
      },
      body: JSON.stringify({ accessToken: token }),
      cache: "no-store",
    });
  } catch {
    return null;
  }

  if (!response.ok) {
    return null;
  }

  let data: RevalidateResponse;
  try {
    data = (await response.json()) as RevalidateResponse;
  } catch {
    // A non-JSON / truncated body means the session is unverifiable, not a server error.
    return null;
  }

  if (!data || !data.active || !data.userId || data.appId !== env.appId) {
    return null;
  }

  return {
    userId: data.userId,
    email: data.email ?? null,
    displayName: data.displayName ?? null,
    role: mapHostRole(data.hostRole),
  };
}
