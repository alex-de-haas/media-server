import { expect, test } from "@playwright/test";
import { aMovie, aTranscodeJob, movieDetail, setupApp } from "./support";

function parts() {
  const detail = movieDetail("m1", "A movie in two parts");
  const source = { versionName: null, container: "mkv", sizeBytes: 1000, bitrate: null, streams: [] };
  return { ...detail, mediaSources: [
    { ...source, id: "part1", fileName: "Movie CD1.mkv", durationTicks: 30 * 60 * 10_000_000 },
    { ...source, id: "part2", fileName: "Movie CD2.mkv", durationTicks: 40 * 60 * 10_000_000 },
  ] };
}

test("join previews both parts, validates selection and submits the chosen order", async ({ page }, testInfo) => {
  await setupApp(page, { library: [aMovie("m1", "A movie in two parts")], detail: { m1: parts() }, transcodeAvailable: true, videoPartJoining: true });
  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Join parts", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByText("New version: Joined · MKV")).toBeVisible();
  await expect(dialog.getByText(/Both originals are kept/)).toBeVisible();
  await expect(dialog.getByText(/Expected duration/)).toBeVisible();
  await dialog.getByLabel("Part 2", { exact: true }).selectOption("part1");
  await expect(dialog.getByRole("button", { name: "Join parts", exact: true })).toBeDisabled();
  await dialog.getByLabel("Part 2", { exact: true }).selectOption("part2");
  await dialog.getByRole("button", { name: "Swap parts" }).click();
  await page.screenshot({ path: testInfo.outputPath("join-parts-dialog.png"), fullPage: true });
  const submitted = page.waitForRequest(request => request.url().endsWith("/transcode/join") && request.method() === "POST");
  await dialog.getByRole("button", { name: "Join parts", exact: true }).click();
  expect((await submitted).postDataJSON()).toEqual({ sourceIds: ["part2", "part1"] });
  await expect(dialog).toBeHidden();
});

test("join reports incompatibility in the dialog and allows correction", async ({ page }) => {
  await setupApp(page, { detail: { m1: parts() }, transcodeAvailable: true, videoPartJoining: true });
  await page.route("**/api/proxy/api/transcode/join", route => route.fulfill({ status: 400, json: { detail: "The parts have different numbers of tracks." } }));
  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Join parts", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("button", { name: "Join parts", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("different numbers of tracks");
  await expect(dialog.getByRole("button", { name: "Swap parts" })).toBeEnabled();
});

test("an older engine does not offer joining", async ({ page }) => {
  await setupApp(page, { detail: { m1: parts() }, transcodeAvailable: true });
  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await expect(page.getByRole("button", { name: "Join parts", exact: true })).toHaveCount(0);
});

test("the job list identifies joining separately from conversion", async ({ page }) => {
  await setupApp(page, { detail: { m1: parts() }, transcodeAvailable: true, videoPartJoining: true,
    transcodeJobs: [{ ...aTranscodeJob("join1", "m1", "Joined movie"), kind: "Join" }] });
  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await expect(page.getByText(/Join parts · 2 files/)).toBeVisible();
});
