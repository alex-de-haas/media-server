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

test("admin sees the Catalogs tab", async ({ page }) => {
  await setupApp(page, { role: "admin" });
  await page.goto("/");
  await expect(page.getByRole("link", { name: "Catalogs", exact: true })).toBeVisible();
});

test("non-admin cannot see or use Catalogs", async ({ page }) => {
  await setupApp(page, { role: "user" });

  await page.goto("/");
  await expect(page.getByRole("link", { name: "Catalogs", exact: true })).toHaveCount(0);

  await page.goto("/catalogs");
  await expect(page.getByText(/administrators only/)).toBeVisible();
});

test("an expired session without a reachable Core shows the sign-in card", async ({ page }) => {
  await setupApp(page, { role: null });
  await page.goto("/");
  await expect(page.getByText("Your Hosty session ended.")).toBeVisible();
  await expect(page.getByText(/machine running Hosty/)).toBeVisible();
});

test("an expired session auto-redirects to Core /open for a fresh code", async ({ page }) => {
  await setupApp(page, { role: null, recoveryOrigin: "http://core.local:7070" });
  await page.route("http://core.local:7070/**", (route) =>
    route.fulfill({ status: 200, contentType: "text/html", body: "<h1>Core login</h1>" }),
  );

  await page.goto("/");
  await page.waitForURL(/core\.local:7070\/api\/apps\/com\.haas\.media-server\/open\?redirectUri=/);
  await expect(page.getByRole("heading", { name: "Core login" })).toBeVisible();
});

test("a denied session shows access denied with no sign-in affordance", async ({ page }) => {
  await setupApp(page, { role: null, sessionStatus: 403 });
  await page.goto("/");
  await expect(page.getByText(/not allowed to use this app/)).toBeVisible();
  await expect(page.getByRole("link", { name: /Sign in/ })).toHaveCount(0);
});
