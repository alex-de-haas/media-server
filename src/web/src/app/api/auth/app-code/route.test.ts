import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";
import { clearRevalidationCache } from "@hosty-sdk/app/server";
import { POST } from "./route";

beforeEach(() => {
  clearRevalidationCache();
  vi.stubEnv("HOSTY_CORE_ORIGIN", "https://core.test");
  vi.stubEnv("HOSTY_APP_SERVICE_TOKEN", "  hosty_app_service.media-server  ");
});

afterEach(() => {
  clearRevalidationCache();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

const verifier = "v".repeat(43);

function exchangeRequest(codeVerifier: unknown = verifier) {
  return new NextRequest("https://media.test/api/auth/app-code", {
    method: "POST",
    headers: { "content-type": "application/json", origin: "https://media.test" },
    body: JSON.stringify({ code: "authorization-code", codeVerifier }),
  });
}

describe("app-code route", () => {
  it.each([null, "", "short", "v".repeat(129), "!".repeat(43)])("refuses an invalid proof locally (%s)", async proof => {
    const coreFetch = vi.fn();
    vi.stubGlobal("fetch", coreFetch);
    const response = await POST(exchangeRequest(proof));
    expect(response.status).toBe(400);
    expect(coreFetch).not.toHaveBeenCalled();
    expect(response.headers.get("set-cookie")).toBeNull();
  });


  it("exchanges with the app service credential and returns only the app grant", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({ accessToken: "hostyg_media-server", expiresInSeconds: 600 }))
      .mockResolvedValueOnce(Response.json({
        active: true,
        appId: "com.haas.media-server",
        userId: "user_1",
        hostRole: "host.admin",
        expiresAt: new Date(Date.now() + 600_000).toISOString(),
      }));
    vi.stubGlobal("fetch", fetchMock);

    const response = await POST(exchangeRequest());

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({ accessToken: "hostyg_media-server", expiresInSeconds: 600 });
    expect(response.headers.get("set-cookie")).toContain("hosty_identity=hostyg_media-server");
    expect(response.headers.get("set-cookie")).not.toContain("hosty_app_service");
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      "https://core.test/api/auth/apps/token",
      expect.objectContaining({
        method: "POST",
        headers: {
          "content-type": "application/json",
          authorization: "Bearer hosty_app_service.media-server",
        },
        body: JSON.stringify({ code: "authorization-code", codeVerifier: verifier }),
        cache: "no-store",
        redirect: "error",
      })
    );
  });

  it.each([undefined, "", "   "])(
    "refuses exchange locally when its service credential is missing (%s)",
    async (serviceToken) => {
      vi.stubEnv("HOSTY_APP_SERVICE_TOKEN", serviceToken);
      const fetchMock = vi.fn();
      vi.stubGlobal("fetch", fetchMock);

      const response = await POST(exchangeRequest());

      expect(response.status).toBe(503);
      expect(await response.json()).toMatchObject({
        code: "app_service_token_missing",
      });
      expect(response.headers.get("set-cookie")).toBeNull();
      expect(fetchMock).not.toHaveBeenCalled();
    }
  );

  it("rejects a Core refusal without setting an app grant cookie", async () => {
    vi.stubGlobal("fetch", vi.fn(async () =>
      Response.json({ code: "invalid_code", message: "Invalid authorization code." }, { status: 401 })
    ));

    const response = await POST(exchangeRequest());

    expect(response.status).toBe(401);
    expect(await response.json()).toMatchObject({ code: "app_auth_code_rejected" });
    expect(response.headers.get("set-cookie")).toBeNull();
  });
});
