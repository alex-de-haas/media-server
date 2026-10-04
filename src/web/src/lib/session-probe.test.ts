import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";
import { clearRevalidationCache } from "@hosty-sdk/app/server";
import { GET } from "../app/api/auth/session/route";

beforeEach(() => {
  vi.stubEnv("HOSTY_CORE_ORIGIN", "http://core.test");
  vi.stubEnv("HOSTY_CORE_PUBLIC_ORIGIN", "https://core.example.test");
  vi.stubEnv("HOSTY_APP_ID", "com.haas.media-server");
  vi.stubEnv("HOSTY_APP_SERVICE_TOKEN", "service-token");
  clearRevalidationCache();
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

it("lets the SDK identify a missing embedded session and the Core recovery destination", async () => {
  const fetch = vi.fn(); vi.stubGlobal("fetch", fetch);
  const response = await GET(new NextRequest("https://media.test/api/auth/session"));
  expect(response.status).toBe(401);
  expect(await response.json()).toMatchObject({ status: "not-present", recovery: {
    appId: "com.haas.media-server", corePublicOrigin: "https://core.example.test",
  } });
  expect(fetch).not.toHaveBeenCalled();
});

it("accepts the popup grant without cookies and exposes active status and renewal metadata", async () => {
  const activeUntil = new Date(Date.now() + 3_600_000).toISOString();
  const fetch = vi.fn(async () => Response.json({ active: true, appId: "com.haas.media-server",
    userId: "user-1", hostRole: "host.admin", expiresAt: activeUntil, activeUntil, activityRequired: true }));
  vi.stubGlobal("fetch", fetch);
  const response = await GET(new NextRequest("https://media.test/api/auth/session", {
    headers: { Authorization: "Bearer hostyg_popup" },
  }));
  expect(response.status).toBe(200);
  expect(await response.json()).toMatchObject({ status: "active", userId: "user-1", role: "admin",
    activeUntil, activityRequired: true, recovery: { appId: "com.haas.media-server" } });
  expect(fetch.mock.calls[0]).toBeDefined();
  expect(response.headers.get("cache-control")).toBe("no-store");
});

it("keeps terminal access denial distinct from recoverable expiry", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => Response.json({ code: "app_access_denied" }, { status: 403 })));
  const response = await GET(new NextRequest("https://media.test/api/auth/session", {
    headers: { Authorization: "Bearer hostyg_denied" },
  }));
  expect(response.status).toBe(403);
  expect((await response.json()).status).toBe("forbidden");
});
