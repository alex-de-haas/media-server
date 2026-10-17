import { expect, test } from "@playwright/test";
import { movieDetail, setupApp } from "./support";

// A 12-hour locale must not turn the explicit 24-hour field into AM/PM in Safari.
test.use({ locale: "en-US", timezoneId: "Europe/Amsterdam" });

test.beforeEach(async ({ page }) => {
  await page.clock.setFixedTime(new Date("2026-10-17T13:50:00Z"));
});

test("watch time displays 24 hours and submits the chosen local instant", async ({ page }, testInfo) => {
  const logged: string[] = [];
  await setupApp(page, { detail: { m1: movieDetail("m1", "Arrival") }, logWatch: (_, time) => logged.push(time) });
  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Log watch…" }).click();
  const dialog = page.getByRole("dialog");
  const date = dialog.getByLabel("Date", { exact: true });
  const time = dialog.getByRole("textbox", { name: "Time (24-hour)" });
  await expect(date).toHaveValue("2026-10-17");
  await expect(time).toHaveValue("15:50");
  await expect(dialog).toHaveCSS("opacity", "1");
  await page.screenshot({ path: testInfo.outputPath("watch-time-24h.png") });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(time).toBeInViewport();
  await expect(dialog.getByRole("button", { name: "Set to now" })).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await page.screenshot({ path: testInfo.outputPath("watch-time-24h-mobile.png") });
  await date.fill("2026-07-04");
  await time.fill("21:15");
  await dialog.getByRole("button", { name: "Log watch", exact: true }).click();
  await expect.poll(() => logged).toEqual(["2026-07-04T19:15:00.000Z"]);
});

test("invalid and future times are blocked, and Now and reopening restore both fields", async ({ page }) => {
  const logged: string[] = [];
  await setupApp(page, { detail: { m1: movieDetail("m1", "Arrival") }, logWatch: (_, time) => logged.push(time) });
  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Log watch…" }).click();
  const dialog = page.getByRole("dialog");
  const date = dialog.getByLabel("Date", { exact: true });
  const time = dialog.getByRole("textbox", { name: "Time (24-hour)" });
  const save = dialog.getByRole("button", { name: "Log watch", exact: true });
  for (const invalid of ["", "24:00", "12:60", "3:50"]) {
    await time.fill(invalid);
    await expect(save).toBeDisabled();
    await expect(time).toHaveAttribute("aria-invalid", "true");
    await expect(dialog.getByText("Enter a date and a 24-hour time (HH:mm).")).toBeVisible();
  }
  await time.fill("15:50");
  await date.fill("2099-01-01");
  await expect(dialog.getByText("That is in the future.")).toBeVisible();
  await expect(save).toBeDisabled();
  await expect(time).toBeVisible();
  await time.fill("12:00");
  await dialog.getByRole("button", { name: "Set to now" }).click();
  await expect(date).toHaveValue("2026-10-17");
  await expect(time).toHaveValue("15:50");
  await expect(save).toBeEnabled();
  await date.fill("2020-01-01");
  await time.fill("00:00");
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Log watch…" }).click();
  await expect(date).toHaveValue("2026-10-17");
  await expect(time).toHaveValue("15:50");
  expect(logged).toEqual([]);
});
