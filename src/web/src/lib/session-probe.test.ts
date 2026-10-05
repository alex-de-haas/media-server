import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";
import { clearRevalidationCache } from "@hosty-sdk/app/server";
import { appFetch, APP_GRANT_STORAGE_KEY, forgetAppGrant, forgetRejectedAppGrant, rememberAppGrant } from "@hosty-sdk/app/browser-auth";
import { GET } from "../app/api/auth/session/route";

beforeEach(() => {
  vi.stubEnv("HOSTY_CORE_ORIGIN", "http://core.test");
  vi.stubEnv("HOSTY_CORE_PUBLIC_ORIGIN", "https://core.example.test");
  vi.stubEnv("HOSTY_APP_ID", "com.haas.media-server");
  vi.stubEnv("HOSTY_APP_SERVICE_TOKEN", "service-token");
  clearRevalidationCache();
});
afterEach(() => { forgetAppGrant(); vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

it("lets the SDK identify a missing embedded session and the Core recovery destination", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>(async () => Response.json({ version: 2 })); vi.stubGlobal("fetch", fetch);
  const response = await GET(new NextRequest("https://media.test/api/auth/session"));
  expect(response.status).toBe(401);
  expect(await response.json()).toMatchObject({ status: "not-present", recovery: {
    appId: "com.haas.media-server", corePublicOrigin: "https://core.example.test", appAuthProtocol: 2,
  } });
  expect(fetch).toHaveBeenCalledOnce();
  const [target, init] = fetch.mock.calls[0]!;
  expect(String(target)).toBe("http://core.test/api/auth/apps/protocol");
  expect(init).toMatchObject({ cache: "no-store", redirect: "error" });
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


it.each([
  [401, "token_revoked", "expired", "unauthenticated", true],
  [401, "token_invalid", "expired", "unauthenticated", true],
  [401, "token_expired", "expired", "unauthenticated", true],
  [403, "token_app_mismatch", "forbidden", "forbidden", true],
  [401, "reauth_required", "expired", "unauthenticated", false],
  [503, "core_unavailable", "unavailable", "core_unavailable", false],
  [403, "app_access_denied", "forbidden", "forbidden", false],
] as const)("passes %s/%s through the probe to the SDK grant lifecycle", async (
  coreStatus, code, status, legacyError, rejected,
) => {
  const values = new Map<string, string>();
  vi.stubGlobal("window", {
    location: { href: "https://media.test/", origin: "https://media.test" },
    self: {}, top: {},
    sessionStorage: {
      get length() { return values.size; },
      key: (index: number) => [...values.keys()][index] ?? null,
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    },
  });
  forgetAppGrant();
  rememberAppGrant("hostyg_embedded");
  expect(values.get(APP_GRANT_STORAGE_KEY)).toBe("hostyg_embedded");
  vi.stubGlobal("fetch", vi.fn(async (url: unknown) => {
    if (String(url).endsWith("/api/auth/apps/protocol")) return Response.json({ version: 2 });
    return Response.json({ code, message: "Private upstream diagnostics must not reach the browser." }, { status: coreStatus });
  }));

  const response = await GET(new NextRequest("https://media.test/api/auth/session", {
    headers: { authorization: "Bearer hostyg_embedded" },
  }));
  const body = await response.json();
  expect(response.status).toBe(coreStatus);
  expect(body.status).toBe(status);
  // ApiError consumers keep the existing string; the SDK bridge reads the nested code.
  expect(body.error).toBe(legacyError);
  expect(body.appSession).toEqual({ status, error: { code } });
  expect(JSON.stringify(body)).not.toContain("Private upstream");
  expect(response.headers.get("cache-control")).toBe("no-store");

  expect(forgetRejectedAppGrant(body.appSession.error.code)).toBe(rejected);
  expect(values.has(APP_GRANT_STORAGE_KEY)).toBe(!rejected);
  const api = vi.fn(async () => Response.json({ ok: true }));
  vi.stubGlobal("fetch", api);
  await appFetch("/api/after-probe", {}, false);
  const [, init] = api.mock.calls[0] as unknown as [string, RequestInit];
  expect(new Headers(init.headers).get("authorization")).toBe(rejected ? null : "Bearer hostyg_embedded");
});
