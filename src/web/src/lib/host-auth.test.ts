import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
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
};

describe("resolveHostSession", () => {
  beforeEach(() => {
    for (const key of ENV_KEYS) {
      savedEnv[key] = process.env[key];
    }
    process.env.HOSTY_CORE_ORIGIN = "http://core.test";
    process.env.HOSTY_APP_ID = "com.haas.media-server";
    process.env.HOSTY_APP_SERVICE_TOKEN = "hosty_app_service.1.x.y";
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

  it("classifies a missing token as expired (recoverable)", async () => {
    expect(await resolveHostSession(null)).toEqual({ status: "expired" });
  });

  it("classifies a missing service token as misconfigured, not a session problem", async () => {
    delete process.env.HOSTY_APP_SERVICE_TOKEN;
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "misconfigured" });
  });

  it("maps Core 401 to expired (recoverable)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(401, { code: "token_expired" })));
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "expired" });
  });

  it("maps Core 403 to denied (terminal)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(403, { code: "user_disabled" })));
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "denied" });
  });

  it("maps Core 5xx to unavailable (transient)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(500, { code: "boom" })));
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "unavailable" });
  });

  it("maps a network failure to unavailable and keeps the session recoverable", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => {
      throw new TypeError("fetch failed");
    }));
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "unavailable" });
  });

  it("maps a non-JSON Core body to unavailable, not expired", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("<html>", { status: 200 })));
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "unavailable" });
  });

  it("treats a token minted for a different app as denied", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => jsonResponse(200, { ...activePayload, appId: "other.app" })),
    );
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "denied" });
  });

  it("treats an active grant without a subject as expired (probe/auth-path consistency)", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => jsonResponse(200, { ...activePayload, userId: "" })),
    );
    expect(await resolveHostSession("hostyg_x")).toEqual({ status: "expired" });
  });

  it("returns the mapped session for a valid grant", async () => {
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
});
