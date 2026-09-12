import { test, expect } from "@playwright/test";
import { setupApp } from "./support";

// Legacy upkeep controls stay removed; catalog maintenance lives in its own Settings tab.
test("general settings has no legacy library upkeep controls", async ({ page }) => {
  await setupApp(page, { role: "admin", transcodeAvailable: true });

  await page.goto("/settings");

  await expect(page.getByRole("heading", { name: "Settings" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Fill in media data" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Run check" })).toHaveCount(0);
  await expect(page.getByText("Removed titles")).toHaveCount(0);
  await expect(page.getByText("Watch history providers", { exact: true })).toHaveCount(0);
});

test("catalog settings tab survives refresh and browser history", async ({ page }) => {
  await setupApp(page, { role: "admin" });
  await page.goto("/settings");
  await expect(page.getByRole("tab", { name: "General", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("button", { name: "Scan all", exact: true })).toHaveCount(0);
  await page.getByRole("tab", { name: "Catalogs", exact: true }).click();
  await expect(page).toHaveURL("/settings?tab=catalogs");
  await expect(page.getByRole("button", { name: "Scan all", exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole("tab", { name: "Catalogs", exact: true })).toHaveAttribute("aria-selected", "true");
  await page.getByRole("tab", { name: "General", exact: true }).click();
  await expect(page).toHaveURL("/settings");
  await page.goBack();
  await expect(page.getByRole("button", { name: "Scan all", exact: true })).toBeVisible();
});

test("removed catalog page returns 404 without redirecting", async ({ page }) => {
  await setupApp(page, { role: "admin" });
  const response = await page.goto("/catalogs");
  expect(response?.status()).toBe(404);
  await expect(page).toHaveURL("/catalogs");
});

for (const width of [390, 1280]) {
  test(`catalog settings fit a ${width}px viewport`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await setupApp(page, { role: "admin" });
    await page.goto("/settings?tab=catalogs");
    await expect(page.getByRole("button", { name: "Scan all", exact: true })).toBeVisible();
    expect(await page.getByRole("main").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    await page.screenshot({ path: `/tmp/media-server-settings-${width}.png`, fullPage: true });
  });
}
