import { test, expect } from "@playwright/test";
import { setupApp } from "./support";

const download = { id: "d1", catalogId: "c1", name: "Inception", state: "Seeding", keepSeeding: true,
  completedAt: "2026-09-23T10:00:00Z", placementStarted: true, uploadRateBytesPerSecond: 1024, sizeBytes: 4096, ratio: 1, peers: 2 };
const item = { id: "i1", catalogId: "c1", downloadId: "d1", downloadName: "Inception", mediaTitle: "Inception", mediaItemId: "m1",
  stage: "Publish", status: "Done", attemptCount: 0, stagesCompleted: ["intake", "download", "identify", "organize", "probe", "enrich", "publish"],
  lastError: null, nextAttemptAt: null, reviewCandidates: [], sourceFiles: [], createdAt: "2026-09-23T10:00:00Z", updatedAt: "2026-09-23T10:00:00Z" };

for (const state of ["Downloading", "Paused"]) {
  test(`seeding indicator toggles both ways while ${state.toLowerCase()}`, async ({ page }) => {
    const current = { ...download, state, completedAt: null, placementStarted: false, keepSeeding: false };
    await setupApp(page, { role: "admin", downloads: [current], ingest: [{ ...item, stage: "Download", status: "Pending", stagesCompleted: ["intake"] }] });
    await page.route("**/api/proxy/api/torrents", (route) => route.fulfill({ json: [current] }));
    await page.route("**/api/proxy/api/torrents/d1/seeding-policy", async (route) => {
      expect(route.request().method()).toBe("PUT");
      current.keepSeeding = route.request().postDataJSON().keepSeeding;
      await route.fulfill({ status: 204 });
    });
    await page.goto("/activity");
    const toggle = page.getByRole("button", { name: "Keep seeding after download", exact: true });
    await expect(toggle).toHaveAttribute("aria-pressed", "false");
    await toggle.hover();
    await expect(page.getByText("Seeding after download: off. Click to turn on.", { exact: true })).toBeVisible();
    await toggle.click();
    await expect(toggle).toHaveAttribute("aria-pressed", "true");
    await expect(toggle).toBeEnabled();
    // The mutation disables the trigger and closes its tooltip. Re-enter after it is enabled.
    await page.getByRole("link", { name: "Activity", exact: true }).hover();
    await toggle.hover();
    await expect(page.getByText("Seeding after download: on. Click to turn off.", { exact: true })).toBeVisible();
    await expect(toggle).toBeEnabled();
    await toggle.focus();
    await page.keyboard.press("Space");
    await expect(toggle).toHaveAttribute("aria-pressed", "false");
    await expect(toggle).toBeEnabled();
  });
}

test("published seed stays Active with completed stages and retained bytes", async ({ page }) => {
  await setupApp(page, { role: "admin", downloads: [download], ingest: [item] });
  await page.goto("/activity");
  await expect(page.getByText("In library / Seeding", { exact: true })).toBeVisible();
  await expect(page.getByText(/retained$/)).toBeVisible();
  await expect(page.getByText("Publish", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove", exact: true })).toHaveCount(0);
  const request = page.waitForRequest((r) => r.url().endsWith("/torrents/d1/stop-seeding") && r.method() === "POST");
  await page.getByRole("button", { name: "Stop seeding and remove originals" }).click();
  await request;
});
for (const action of ["Retry", "Stop seeding and continue"]) {
  test(`capacity warning offers ${action} at Organize`, async ({ page }) => {
    await setupApp(page, { role: "admin", downloads: [download], ingest: [{ ...item, mediaItemId: null, stage: "Organize", status: "AwaitingSpace", stagesCompleted: ["intake", "download", "identify"], lastError: "4096 bytes required, 0 bytes available." }] });
    await page.goto("/activity");
    await expect(page.getByRole("alert").filter({ hasText: "4096 bytes required" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Retry", exact: true })).toHaveCount(1);
    await expect(page.getByRole("button", { name: "Stop seeding and continue", exact: true })).toHaveCount(1);
    const suffix = action === "Retry" ? "/ingest/i1/retry" : "/torrents/d1/stop-seeding";
    const request = page.waitForRequest((r) => r.url().endsWith(suffix) && r.method() === "POST");
    await page.getByRole("button", { name: action, exact: true }).click();
    await request;
  });
}

test("cleanup previews exact owned candidates and protects unknown roots", async ({ page }) => {
  await setupApp(page, { role: "admin" });
  await page.route("**/api/proxy/api/settings/temporary-downloads/", (route) => route.fulfill({ json: [
    { id: "owned", catalogId: "c1", catalog: "Movies", path: ".incoming/owned", bytes: 4096, state: "Ready for cleanup", canClean: true },
    { id: null, catalogId: "c1", catalog: "Movies", path: ".incoming/unknown", bytes: 4096, state: "Unknown legacy directory", canClean: false },
  ] }));
  await page.route("**/api/proxy/api/settings/temporary-downloads/clean", (route) => route.fulfill({ json: [{ id: "owned", cleaned: true }] }));
  await page.goto("/settings");
  await page.getByRole("button", { name: "Analyze temporary files" }).click();
  await expect(page.getByRole("checkbox").nth(1)).toBeDisabled();
  await page.getByRole("checkbox").nth(0).check();
  await page.getByRole("button", { name: "Preview cleanup (1)" }).click();
  await expect(page.getByRole("listitem").filter({ hasText: "Movies / .incoming/owned" })).toBeVisible();
  const request = page.waitForRequest((r) => r.url().endsWith("/settings/temporary-downloads/clean") && r.method() === "POST");
  await page.getByRole("button", { name: "Clean selected" }).click();
  expect((await request).postDataJSON()).toEqual({ ids: ["owned"] });
});

test("temporary cleanup is hidden from regular users", async ({ page }) => {
  await setupApp(page, { role: "user" });
  await page.goto("/settings");
  await expect(page.getByRole("button", { name: "Analyze temporary files" })).toHaveCount(0);
});
