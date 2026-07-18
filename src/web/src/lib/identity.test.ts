import { describe, expect, it } from "vitest";
import { buildCoreOpenUrl, identityCookieAttributes, isLoopbackHost, mapHostRole } from "./identity";

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

describe("isLoopbackHost", () => {
  it("recognizes loopback hosts", () => {
    expect(isLoopbackHost("localhost")).toBe(true);
    expect(isLoopbackHost("127.0.0.1")).toBe(true);
    expect(isLoopbackHost("LOCALHOST")).toBe(true);
    expect(isLoopbackHost("[::1]")).toBe(true);
  });

  it("rejects everything else", () => {
    expect(isLoopbackHost("media.example.com")).toBe(false);
    expect(isLoopbackHost("192.168.1.50")).toBe(false);
  });
});

describe("buildCoreOpenUrl", () => {
  const page = {
    origin: "http://127.0.0.1:61679",
    pathname: "/movies",
    search: "?sort=title",
    hostname: "127.0.0.1",
  };

  it("builds the /open URL with the current page as redirect target", () => {
    const url = buildCoreOpenUrl("http://127.0.0.1:7070", "com.haas.media-server", page);
    expect(url).toBe(
      "http://127.0.0.1:7070/api/apps/com.haas.media-server/open?redirectUri=" +
        encodeURIComponent("http://127.0.0.1:61679/movies?sort=title"),
    );
  });

  it("percent-encodes the app id in the path", () => {
    const url = buildCoreOpenUrl("https://core.example.com", "com/haas app", page);
    expect(url).toContain("/api/apps/com%2Fhaas%20app/open");
  });

  it("returns null without a Core public origin", () => {
    expect(buildCoreOpenUrl(null, "com.haas.media-server", page)).toBeNull();
  });

  it("returns null for an unparsable Core origin", () => {
    expect(buildCoreOpenUrl("not a url", "com.haas.media-server", page)).toBeNull();
  });

  it("refuses a loopback Core origin when the page is not on loopback", () => {
    const remotePage = { ...page, origin: "http://192.168.1.50:61679", hostname: "192.168.1.50" };
    expect(buildCoreOpenUrl("http://127.0.0.1:7070", "com.haas.media-server", remotePage)).toBeNull();
  });

  it("allows a domain Core origin regardless of the page host", () => {
    const remotePage = { ...page, origin: "https://media.example.com", hostname: "media.example.com" };
    expect(
      buildCoreOpenUrl("https://core.example.com", "com.haas.media-server", remotePage),
    ).toContain("https://core.example.com/api/apps/");
  });
});
