import { describe, expect, it } from "vitest";
import { identityCookieAttributes, mapHostRole } from "./identity";

describe("mapHostRole", () => {
  it("maps host admins to admin", () => {
    expect(mapHostRole("host.admin")).toBe("admin");
    expect(mapHostRole("HOST.ADMIN")).toBe("admin");
  });

  it("maps everything else to user", () => {
    expect(mapHostRole("host.user")).toBe("user");
    expect(mapHostRole(null)).toBe("user");
    expect(mapHostRole(undefined)).toBe("user");
  });
});

describe("identityCookieAttributes", () => {
  it("uses SameSite=None; Secure over https (cross-site iframe)", () => {
    const attributes = identityCookieAttributes(true, 3600);
    expect(attributes.secure).toBe(true);
    expect(attributes.sameSite).toBe("none");
    expect(attributes.maxAge).toBe(3600);
    expect(attributes.httpOnly).toBe(true);
    expect(attributes.path).toBe("/");
  });

  it("falls back to SameSite=Lax without Secure over plain http", () => {
    const attributes = identityCookieAttributes(false, 120.9);
    expect(attributes.secure).toBe(false);
    expect(attributes.sameSite).toBe("lax");
    expect(attributes.maxAge).toBe(120);
  });

  it("never emits a negative max-age", () => {
    expect(identityCookieAttributes(true, -5).maxAge).toBe(0);
  });
});
