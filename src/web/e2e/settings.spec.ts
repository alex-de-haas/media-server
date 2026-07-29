import { test, expect } from "@playwright/test";
import { setupApp } from "./support";

test("the media backfill runs and reports what it filled in", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    transcodeAvailable: true,
    mediaBackfill: { itemsRefreshed: 2, remaining: 1, sidecarsFilled: 4 },
  });

  await page.goto("/settings");

  const posted = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/backfill-media") && request.method() === "POST",
  );
  await page.getByRole("button", { name: "Fill in media data" }).click();
  await posted;

  // The counts are the point: an operator ran this to learn whether their sidecars were filled.
  await expect(page.getByText(/2 title\(s\) re-probed, 4 sidecar track\(s\) filled/)).toBeVisible();
  // And what is still unanswered, with the usual reason.
  await expect(page.getByText(/1 source\(s\) still without engine data/)).toBeVisible();
});

test("without the transcode engine the backfill says why instead of offering a no-op", async ({ page }) => {
  // Everything it does is read through the engine's probe. Without it the pass would re-read the same files
  // with the header parser and change nothing.
  await setupApp(page, { role: "admin", transcodeAvailable: false });

  await page.goto("/settings");

  await expect(page.getByRole("button", { name: "Fill in media data" })).toBeDisabled();
  await expect(page.getByText(/Needs the transcode engine/)).toBeVisible();
});

test("a plain viewer is not offered the backfill at all", async ({ page }) => {
  await setupApp(page, { role: "user", transcodeAvailable: true });

  await page.goto("/settings");

  await expect(page.getByRole("button", { name: "Fill in media data" })).toHaveCount(0);
});
