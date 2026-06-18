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

test("an unauthenticated session shows the sign-in hint", async ({ page }) => {
  await setupApp(page, { role: null });
  await page.goto("/");
  await expect(page.getByText(/Open this app from the Hosty Shell/)).toBeVisible();
});
