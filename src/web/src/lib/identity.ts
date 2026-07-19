// Pure identity helpers, free of Next.js request types so they are trivially unit-testable.
// Cookie attributes, loopback detection and the Core /open URL builder moved to
// @hosty-sdk/app — only the app's own role model lives here.

export type AppRole = "admin" | "user";

/** Hosty admins map to the Media Server `admin` role; every other Host role maps to `user`. */
export function mapHostRole(hostRole: string | null | undefined): AppRole {
  return hostRole?.toLowerCase() === "host.admin" ? "admin" : "user";
}
