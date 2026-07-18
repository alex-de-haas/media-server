import type { NextRequest } from "next/server";
import { hostyServerEnv } from "@/lib/hosty";
import { type AppRole, identityCookieAttributes, mapHostRole } from "@/lib/identity";

/** App-origin HttpOnly cookie holding the Host identity token. */
export const IDENTITY_COOKIE = "hosty_identity";

// Core runs on the same host as the app services, so a short budget is plenty; a stalled call
// must not hold the session route open indefinitely.
const CORE_AUTH_TIMEOUT_MS = 1_500;

export interface HostSession {
  userId: string;
  email: string | null;
  displayName: string | null;
  role: AppRole;
}

/**
 * Classified outcome of revalidating the identity token against Core, following the platform
 * identity error contract (401 recoverable / 403 terminal):
 * - `expired`: recoverable — no token, Core 401, or an unusable grant; re-authorize via Shell
 *   or Core `/open`.
 * - `denied`: terminal — Core 403 or a token minted for a different app; never auto-redirect
 *   (it would loop).
 * - `unavailable`: transient — Core unreachable, slow, or answering garbage; keep the cookie
 *   and let the user retry.
 * - `misconfigured`: operator problem — the app service token is missing; signing in cannot
 *   fix it, so the UI must not offer a login.
 */
export type SessionResolution =
  | { status: "active"; session: HostSession }
  | { status: "expired" }
  | { status: "denied" }
  | { status: "unavailable" }
  | { status: "misconfigured" };

export type SessionFailureStatus = Exclude<SessionResolution["status"], "active">;

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
 * Revalidates a forwarded identity token against Core with the app service token and classifies
 * the outcome. Core identity tokens are opaque (`hostyg_` grants), so this round-trip is the
 * only trustworthy validation.
 */
export async function resolveHostSession(token: string | null): Promise<SessionResolution> {
  if (!token) {
    return { status: "expired" };
  }

  const env = hostyServerEnv();
  if (!env.serviceToken) {
    return { status: "misconfigured" };
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
      signal: AbortSignal.timeout(CORE_AUTH_TIMEOUT_MS),
    });
  } catch {
    // Network failure or timeout — transient either way; keep the cookie.
    return { status: "unavailable" };
  }

  if (!response.ok) {
    return {
      status: response.status === 401 ? "expired" : response.status === 403 ? "denied" : "unavailable",
    };
  }

  let data: RevalidateResponse;
  try {
    data = (await response.json()) as RevalidateResponse;
  } catch {
    // A non-JSON / truncated body means the session is unverifiable, not proof it is invalid.
    return { status: "unavailable" };
  }

  if (data?.appId && data.appId !== env.appId) {
    // A token minted for a different app is Core's token_app_mismatch — terminal, like a 403.
    return { status: "denied" };
  }

  // An "active" grant without a subject is unusable; classify it as recoverable so the probe
  // and the real auth path can never disagree about the same token.
  if (!data || data.active !== true || !data.userId) {
    return { status: "expired" };
  }

  return {
    status: "active",
    session: {
      userId: data.userId,
      email: data.email ?? null,
      displayName: data.displayName ?? null,
      role: mapHostRole(data.hostRole),
    },
  };
}
