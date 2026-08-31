import { test, expect } from "@playwright/test";
import { setupApp } from "./support";

// Settings used to carry the library's upkeep: a health check, a media-data backfill, and the list of
// removed titles. All three are gone — the first two are what a catalog scan and a metadata refresh do
// on the Catalogs page, and removed titles live behind the Movies/Series grids' own toggle.
test("settings no longer carries the library's upkeep controls", async ({ page }) => {
  await setupApp(page, { role: "admin", transcodeAvailable: true });

  await page.goto("/settings");

  await expect(page.getByRole("heading", { name: "Settings" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Fill in media data" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Run check" })).toHaveCount(0);
  await expect(page.getByText("Removed titles")).toHaveCount(0);
});
