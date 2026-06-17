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
