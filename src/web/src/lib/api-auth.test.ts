import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { forgetAppGrant, rememberAppGrant } from "@hosty-sdk/app/browser-auth";
import { apiJson } from "./api";

beforeEach(() => {
  vi.stubGlobal("window", { location: { href: "https://media.test/", origin: "https://media.test" } });
  forgetAppGrant();
});
afterEach(() => { forgetAppGrant(); vi.unstubAllGlobals(); });

it("uses the SDK popup grant for app requests when the iframe has no cookie", async () => {
  const fetch = vi.fn(async () => Response.json({ userId: "user-1" }));
  vi.stubGlobal("fetch", fetch);
  rememberAppGrant("hostyg_popup");
  expect(await apiJson("/api/auth/session")).toEqual({ userId: "user-1" });
  const [, init] = fetch.mock.calls[0] as unknown as [string, RequestInit];
  expect(new Headers(init.headers).get("authorization")).toBe("Bearer hostyg_popup");
  expect(init.redirect).toBe("error");
});

it("never sends the remembered grant to a different origin", async () => {
  const fetch = vi.fn(); vi.stubGlobal("fetch", fetch);
  rememberAppGrant("hostyg_popup");
  await expect(apiJson("https://another.test/api")).rejects.toThrow("origin");
  expect(fetch).not.toHaveBeenCalled();
});
