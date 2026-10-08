import { test, expect } from "@playwright/test";
import { aMovie, setupApp } from "./support";

test("renders the shell and navigates between tabs", async ({ page }) => {
  await setupApp(page, { recent: [aMovie("m1", "Arrival")] });
  await page.goto("/");

  await expect(page.getByRole("heading", { name: "Media Server" })).toBeVisible();

  await page.getByRole("link", { name: "Movies" }).click();
  await expect(page).toHaveURL(/\/movies$/);
  await expect(page.getByRole("heading", { name: "Movies" })).toBeVisible();
});

test("direct navigation and refresh survive", async ({ page }) => {
  await setupApp(page, { library: [aMovie("m1", "Arrival")] });

  await page.goto("/movies");
  await expect(page.getByRole("heading", { name: "Movies" })).toBeVisible();

  await page.reload();
  await expect(page.getByRole("heading", { name: "Movies" })).toBeVisible();
});

test("catalog management is absent from primary navigation", async ({ page }) => {
  await setupApp(page, { role: "admin" });
  await page.goto("/");
  await expect(page.getByRole("navigation").getByRole("link", { name: "Catalogs", exact: true })).toHaveCount(0);
});

test("non-admin cannot see or use Catalogs", async ({ page }) => {
  await setupApp(page, { role: "user" });

  await page.goto("/");
  await expect(page.getByRole("link", { name: "Catalogs", exact: true })).toHaveCount(0);

  await page.goto("/settings?tab=catalogs");
  await expect(page.getByRole("tab", { name: "General", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("tab", { name: "Catalogs", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Scan all", exact: true })).toHaveCount(0);
});

test("an expired session without verified recovery shows unavailable and Retry", async ({ page, baseURL }) => {
  await setupApp(page, { role: null });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Cannot reach Hosty" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Retry", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sign in via Hosty" })).toHaveCount(0);
  await expect(page).toHaveURL(`${baseURL}/`);
});

test("a configured Core origin with unknown protocol never navigates to Core", async ({ page, baseURL }) => {
  const requests: string[] = [];
  await setupApp(page, { role: null, recoveryOrigin: "http://core.local:7070", appAuthProtocol: null });
  await page.route("http://core.local:7070/**", async route => {
    requests.push(route.request().method());
    await route.abort();
  });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Cannot reach Hosty" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Retry", exact: true })).toBeVisible();
  await expect(page).toHaveURL(`${baseURL}/`);
  expect(requests).toEqual([]);
});

test("an expired session posts an app-origin protocol-2 intent with public S256 proof", async ({ page, baseURL }) => {
  const coreOrigin = "http://core.local:7070";
  const appOrigin = new URL(baseURL!).origin;
  let intentCount = 0;
  const publicIntent = { state: "", challenge: "", redirectUri: "" };
  await setupApp(page, { role: null, recoveryOrigin: coreOrigin, appAuthProtocol: 2 });
  await page.route(`${coreOrigin}/**`, async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname === "/api/apps/com.haas.media-server/sign-in-intent") {
      intentCount++;
      expect(request.method()).toBe("POST");
      expect(request.isNavigationRequest()).toBe(true);
      expect(request.headers().origin).toBe(appOrigin);
      expect(url.search).toBe("");
      const fields = new URLSearchParams(request.postData() ?? "");
      expect([...fields.keys()].sort()).toEqual(["codeChallenge", "codeChallengeMethod", "redirectUri", "state"]);
      expect(fields.get("codeChallengeMethod")).toBe("S256");
      expect(fields.get("state")).toMatch(/^[a-f0-9]{64}$/);
      expect(fields.get("codeChallenge")).toMatch(/^[A-Za-z0-9_-]{43}$/);
      expect(fields.has("codeVerifier")).toBe(false);
      Object.assign(publicIntent, { state: fields.get("state"), challenge: fields.get("codeChallenge"), redirectUri: fields.get("redirectUri") });
      const callback = new URL(fields.get("redirectUri")!);
      expect(callback.origin).toBe(appOrigin);
      expect(callback.pathname).toBe("/");
      expect(callback.searchParams.get("state")).toBe(fields.get("state"));
      expect([...callback.searchParams.keys()]).toEqual(["state"]);
      // This fixture verifies the app request boundary. Real Core nonce/continuation
      // behavior is covered by Core-managed acceptance, not this mocked login page.
      await route.fulfill({ status: 200, contentType: "text/html", body: "<h1>Core login</h1>" });
      return;
    }
    throw new Error("Unexpected Core request outside the intent fixture.");
  });

  await page.goto("/");
  await page.waitForURL(`${coreOrigin}/api/apps/com.haas.media-server/sign-in-intent`);
  await expect(page.getByRole("heading", { name: "Core login" })).toBeVisible();
  // Return without a callback state: the redirect guard keeps the app document mounted,
  // allowing inspection of its own stored proof after the paused navigation has completed.
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Sign in via Hosty" })).toBeVisible();
  // Verify the public challenge against the app-owned private attempt without returning
  // the verifier from the browser or exposing it in a request/assertion payload.
  const locallyBound = await page.evaluate(async ({ state, challenge, redirectUri }) => {
    const attempt = JSON.parse(sessionStorage.getItem(`hosty.auth.attempt:${state}`) ?? "null");
    if (!attempt || attempt.protocol !== 2 || attempt.state !== state || attempt.redirectUri !== redirectUri ||
        typeof attempt.codeVerifier !== "string" || !/^[A-Za-z0-9._~-]{43,128}$/.test(attempt.codeVerifier)) return false;
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(attempt.codeVerifier)));
    const derived = btoa(String.fromCharCode(...digest)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    return derived === challenge && attempt.codeChallenge === challenge;
  }, publicIntent);
  expect(locallyBound).toBe(true);
  expect(intentCount).toBe(1);
});

test("a denied session shows access denied with no sign-in affordance", async ({ page }) => {
  await setupApp(page, { role: null, sessionStatus: 403 });
  await page.goto("/");
  await expect(page.getByText("Your account does not have access to this app.")).toBeVisible();
  await expect(page.getByRole("link", { name: /Sign in/ })).toHaveCount(0);
});
