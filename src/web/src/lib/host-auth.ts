import type { NextRequest } from "next/server";
import {
  getRecoveryParams,
  readAppIdentityToken,
  resolveAppSession,
  type AppSessionResolution,
  type HostyAppConfig,
} from "@hosty-sdk/app/server";
import type { AppSessionFailureStatus } from "@hosty-sdk/app";
import { mapHostRole, type AppRole } from "@/lib/identity";

/** App-origin HttpOnly cookie holding the Host identity token. */
export const IDENTITY_COOKIE = "hosty_identity";

/** This app's SDK configuration: the cookie namespace and role model stay app-owned. */
export const hostyAppConfig: HostyAppConfig = {
  appIdFallback: "com.haas.media-server",
  identityCookieName: IDENTITY_COOKIE,
  mapHostRole,
};

export interface HostSession {
  userId: string;
  email: string | null;
  displayName: string | null;
  role: AppRole;
}

export type SessionFailureStatus = AppSessionFailureStatus;

export type SessionResolution =
  | { status: "active"; session: HostSession }
  | { status: SessionFailureStatus };

export function readIdentityToken(request: NextRequest): string | null {
  return readAppIdentityToken(request.headers, hostyAppConfig);
}

export function readSessionRecoveryParams() {
  return getRecoveryParams(hostyAppConfig);
}

/**
 * Revalidates a forwarded identity token against Core (via the SDK: classification per the
 * platform contract, 30s positive cache, no negative caching) and maps the identity onto
 * this app's session shape.
 */
export async function resolveHostSession(token: string | null): Promise<SessionResolution> {
  const resolution: AppSessionResolution = await resolveAppSession(token, hostyAppConfig);
  if (resolution.status !== "active") {
    return { status: resolution.status };
  }
  return {
    status: "active",
    session: {
      userId: resolution.identity.userId,
      email: resolution.identity.email,
      displayName: resolution.identity.displayName,
      // The app's own role mapper applied to the raw host role: no assertion needed, and
      // an unexpected SDK appRole value can never leak into the session contract.
      role: mapHostRole(resolution.identity.hostRole),
    },
  };
}
