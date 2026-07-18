// Pure identity helpers, free of Next.js request types so they are trivially unit-testable.

export type AppRole = "admin" | "user";

/** Hosty admins map to the Media Server `admin` role; every other Host role maps to `user`. */
export function mapHostRole(hostRole: string | null | undefined): AppRole {
  return hostRole?.toLowerCase() === "host.admin" ? "admin" : "user";
}

export interface IdentityCookieAttributes {
  httpOnly: true;
  secure: boolean;
  sameSite: "none" | "lax";
  path: "/";
  maxAge: number;
}

/**
 * Cookie attributes for the app-origin identity cookie. In a cross-site Shell iframe https must
 * use `SameSite=None; Secure`; plain http (local dev) cannot set `Secure`, so it falls back to
 * `SameSite=Lax`.
 */
export function identityCookieAttributes(secure: boolean, maxAgeSeconds: number): IdentityCookieAttributes {
  return {
    httpOnly: true,
    secure,
    sameSite: secure ? "none" : "lax",
    path: "/",
    maxAge: Math.max(0, Math.floor(maxAgeSeconds)),
  };
}

/** True for hosts that only resolve on the machine itself. */
export function isLoopbackHost(hostname: string): boolean {
  const host = hostname.toLowerCase();
  return host === "localhost" || host === "127.0.0.1" || host === "::1" || host === "[::1]";
}

/**
 * Builds the Core standalone re-auth URL (`/api/apps/{id}/open?redirectUri=…`) for the current
 * page, or null when the redirect is known to be impossible:
 * - no Core public origin configured/injected at all, or an unparsable one;
 * - Core's origin is loopback while this page is not — an unset public origin falls back to
 *   Core's loopback listen URL, which only a same-machine browser can follow, and Core would
 *   reject the foreign redirect URI anyway (`redirect_uri_denied`). Firing that redirect would
 *   strand the user on their own machine's port instead of a login page.
 * The fragment is deliberately dropped: Core rejects redirect URIs carrying one
 * (`redirect_uri_invalid`), and it would not survive the server redirect anyway.
 */
export function buildCoreOpenUrl(
  corePublicOrigin: string | null,
  appId: string,
  location: { origin: string; pathname: string; search: string; hostname: string },
): string | null {
  if (!corePublicOrigin) {
    return null;
  }

  let target: URL;
  try {
    target = new URL(`/api/apps/${encodeURIComponent(appId)}/open`, corePublicOrigin);
  } catch {
    return null;
  }

  if (isLoopbackHost(target.hostname) && !isLoopbackHost(location.hostname)) {
    return null;
  }

  target.searchParams.set("redirectUri", `${location.origin}${location.pathname}${location.search}`);
  return target.toString();
}
