import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearRevalidationCache } from "@hosty-sdk/app/server";
import { resolveHostSession } from "./host-auth";

const ENV_KEYS = ["HOSTY_CORE_ORIGIN", "HOSTY_APP_ID", "HOSTY_APP_SERVICE_TOKEN"] as const;
const savedEnv: Partial<Record<(typeof ENV_KEYS)[number], string | undefined>> = {};

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

const activePayload = {
  active: true,
  appId: "com.haas.media-server",
  userId: "user_1",
  email: "user@example.com",
  displayName: "User",
  hostRole: "host.admin",
  expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
};

describe("resolveHostSession", () => {
  beforeEach(() => {
    for (const key of ENV_KEYS) {
      savedEnv[key] = process.env[key];
    }
    process.env.HOSTY_CORE_ORIGIN = "http://core.test";
    process.env.HOSTY_APP_ID = "com.haas.media-server";
    process.env.HOSTY_APP_SERVICE_TOKEN = "hosty_app_service.1.x.y";
    // The SDK keeps a process-global positive cache; isolate every test.
    clearRevalidationCache();
  });

  afterEach(() => {
    for (const key of ENV_KEYS) {
      if (savedEnv[key] === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = savedEnv[key];
      }
    }
    vi.unstubAllGlobals();
  });

  it("classifies a missing token as not-present (recoverable, no Core call)", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    expect(await resolveHostSession(null)).toEqual({ status: "not-present" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("classifies a missing service token as misconfigured, not a session problem", async () => {
    delete process.env.HOSTY_APP_SERVICE_TOKEN;
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "misconfigured" });
  });

  it("maps Core 401 to expired (recoverable)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(401, { code: "token_expired" })));
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "expired" });
  });

  it("maps Core 403 to forbidden (terminal)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(403, { code: "user_disabled" })));
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "forbidden" });
  });

  it("maps Core 5xx and network failures to unavailable (transient)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(500, { code: "boom" })));
    expect(await resolveHostSession("hostyg_a")).toEqual({ status: "unavailable" });
    vi.stubGlobal("fetch", vi.fn(async () => {
      throw new TypeError("fetch failed");
    }));
    expect(await resolveHostSession("hostyg_b")).toEqual({ status: "unavailable" });
    vi.stubGlobal("fetch", vi.fn(async () => new Response("<html>", { status: 200 })));
    expect(await resolveHostSession("hostyg_c")).toEqual({ status: "unavailable" });
  });

  it("treats a token minted for a different app as forbidden", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => jsonResponse(200, { ...activePayload, appId: "other.app" })),
    );
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "forbidden" });
  });

  it("treats an active grant without a subject as expired (probe/auth-path consistency)", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => jsonResponse(200, { ...activePayload, userId: "" })),
    );
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "expired" });
  });

  it("returns the mapped session for a valid grant, applying the app role model", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, activePayload));
    vi.stubGlobal("fetch", fetchMock);

    expect(await resolveHostSession("hostyg_x")).toEqual({
      status: "active",
      session: { userId: "user_1", email: "user@example.com", displayName: "User", role: "admin" },
    });

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("http://core.test/api/auth/apps/revalidate");
    expect(new Headers(init.headers).get("authorization")).toBe("Bearer hosty_app_service.1.x.y");
    expect(JSON.parse(init.body as string)).toEqual({ accessToken: "hostyg_x" });
  });

  it("maps non-admin host roles to the user app role", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => jsonResponse(200, { ...activePayload, hostRole: "host.user" })),
    );
    const resolution = await resolveHostSession("hostyg_x");
    expect(resolution.status === "active" && resolution.session.role).toBe("user");
  });
});
