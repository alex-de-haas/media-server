import { afterEach, expect, it, vi } from "vitest";
import { clearRevalidationCache } from "@hosty-sdk/app/server";
import { GET } from "./route";

afterEach(() => { clearRevalidationCache(); vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

function core(role: string, required: string[] = [], granted: string[] = []) {
  vi.stubEnv("HOSTY_APP_ID", "com.haas.media-server");
  vi.stubEnv("HOSTY_CORE_ORIGIN", "https://core.test");
  vi.stubEnv("HOSTY_CORE_PUBLIC_ORIGIN", "https://core.test");
  vi.stubEnv("HOSTY_APP_SERVICE_TOKEN", "private-service");
  const fetcher = vi.fn(async (url: unknown, init?: RequestInit) => {
    if (String(url).endsWith("/protocol")) return Response.json({ version: 2 });
    if (String(url).endsWith("/revalidate")) {
      expect(JSON.parse(init?.body as string).accessToken).toBe("hostyg_current");
      return Response.json({ active: true, appId: "com.haas.media-server", userId: "host-user", hostRole: role,
        expiresAt: new Date(Date.now() + 60000).toISOString() });
    }
    return Response.json({ required, optional: ["apps.logs"], granted });
  });
  vi.stubGlobal("fetch", fetcher);
  return fetcher;
}

it("returns recovery before authentication without reading permissions", async () => {
  const fetcher = core("host.user");
  const response = await GET(new Request("https://app.test/api/hosty/session"));
  expect(await response.json()).toMatchObject({ status: "not-present", recovery: { appId: "com.haas.media-server", appAuthProtocol: 2 } });
  expect(fetcher.mock.calls.every(([url]) => String(url).endsWith("/protocol"))).toBe(true);
});

it.each(["host.admin", "host.user"])("uses raw Host role %s for required-setup controls", async role => {
  core(role, ["apps.read"]);
  const response = await GET(new Request("https://app.test/api/hosty/session", {
    headers: { authorization: "Bearer hostyg_current", cookie: "hosty_identity=stale" },
  }));
  const body = await response.json();
  expect(body).toMatchObject({ status: "active", userId: "host-user", hosty: { version: 1, setup: "missing" } });
  expect(Boolean(body.hosty.notice)).toBe(role === "host.admin");
  expect(response.headers.get("cache-control")).toBe("no-store");
  expect(JSON.stringify(body)).not.toContain("private-service");
});

it("retains the app cookie namespace and ignores missing optional grants", async () => {
  core("host.user");
  const response = await GET(new Request("https://app.test/api/hosty/session", {
    headers: { cookie: "hosty_identity=hostyg_current" },
  }));
  expect(await response.json()).toMatchObject({ status: "active", hosty: { setup: "ready" } });
});
