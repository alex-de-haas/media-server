import { expect, test } from "@playwright/test";
import { movieDetail, setupApp, aTranscodeJob } from "./support";

function discDetail() {
  return { ...movieDetail("m1", "Blu-ray film"), mediaSources: [{
    id: "disc1", versionName: "Blu-ray", container: "bdmv", fileName: "Film - Blu-ray", sourceKind: "Bluray",
    playbackAvailability: "RequiresConversion", sizeBytes: 46_000_000_000, bitrate: null, durationTicks: 0, streams: [],
  }] };
}

test("a disc needs conversion and an older engine cannot create MKV", async ({ page }) => {
  await setupApp(page, { detail: { m1: discDetail() }, transcodeAvailable: true });
  await page.goto("/movies/m1");
  await expect(page.getByText("Requires conversion to MKV before playback.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Create MKV", exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Play in Infuse", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Convert to a smaller version" })).toHaveCount(0);
});

test("playlist and explicit track settings become a separate MKV job", async ({ page }) => {
  await setupApp(page, { detail: { m1: discDetail() }, transcodeAvailable: true });
  await page.route("**/api/proxy/api/transcode/availability", route => route.fulfill({ json: { available: true, blurayImport: true } }));
  const tracks = [
    { id: 0, type: "video", codec: "HEVC", pixelDimensions: "3840x2160" },
    { id: 2, type: "audio", codec: "DTS-HD", language: "en", default: true, forced: false, channels: 8 },
    { id: 3, type: "audio", codec: "AC-3", language: "ru", default: false, forced: false, channels: 6 },
    { id: 4, type: "subtitles", codec: "PGS", language: "en", default: false, forced: true },
  ].map(t => ({ title: null, language: null, dolbyVision: null, ...t }));
  await page.route("**/api/proxy/api/transcode/bluray/disc1*", route => route.fulfill({ json: {
    revision: "abc", sizeBytes: 1000, playlists: [{ id: "00001", durationSeconds: 7200, chapters: 17, clips: ["00011.m2ts"], tracks: route.request().url().includes("playlistId") ? tracks : [], error: null }],
  } }));
  await page.route("**/api/proxy/api/transcode/bluray", route => route.fulfill({ json: { ...aTranscodeJob("b1", "m1", "Blu-ray MKV"), kind: "Bluray" } }));
  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "Create MKV", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("Playlist", { exact: true }).selectOption("00001");
  await expect(dialog.getByText(/Primary video: HEVC/)).toBeVisible();
  await dialog.getByLabel(/Track 3:/).check();
  await dialog.getByLabel(/Track 2:/).uncheck();
  await dialog.getByLabel(/Track 4:/).check();
  const submitted = page.waitForRequest(r => r.url().endsWith("/transcode/bluray") && r.method() === "POST");
  await dialog.getByRole("button", { name: "Create MKV", exact: true }).click();
  const payload = (await submitted).postDataJSON();
  expect(payload.selection.playlistId).toBe("00001");
  expect(payload.selection.revision).toBe("abc");
  expect(payload.selection.videoTrackId).toBe(0);
  expect(payload.selection.audio.map((t: { id: number }) => t.id)).toEqual([3]);
  expect(payload.selection.subtitles).toEqual([{ id: 4, language: "en", title: null, default: false, forced: true }]);
  await expect(dialog).toBeHidden();
});
