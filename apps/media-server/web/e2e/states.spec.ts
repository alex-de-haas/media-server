import { test, expect } from "@playwright/test";
import { setupApp } from "./support";

test("shows an empty state when there are no movies", async ({ page }) => {
  await setupApp(page, { library: [] });
  await page.goto("/movies");
  await expect(page.getByText("No published items yet.")).toBeVisible();
});

test("shows an error state when the library fails to load", async ({ page }) => {
  await setupApp(page, { library: { status: 500 } });
  await page.goto("/movies");
  await expect(page.getByText(/Couldn.t load this/)).toBeVisible();
});
