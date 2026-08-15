import { test, expect } from "@playwright/test";
import { anEpisode, aMovie, aSeason, aSeries, aUserData, movieDetail, seriesDetail, setupApp } from "./support";

test("opens a movie detail page and marks it watched", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival") },
  });

  await page.goto("/movies");
  await page.getByRole("link", { name: /Arrival/ }).click();
  await expect(page).toHaveURL(/\/movies\/m1$/);
  await expect(page.getByRole("heading", { name: "Arrival" })).toBeVisible();

  const played = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/played") && request.method() === "POST",
  );
  await page.getByRole("button", { name: "Mark watched" }).click();
  await played;
});

test("rates a movie from the detail page", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival") },
  });

  await page.goto("/movies/m1");

  const rated = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/rating") && request.method() === "PUT",
  );
  await page.getByRole("button", { name: "Rate 4 stars" }).click();
  expect((await rated).postDataJSON()).toEqual({ rating: 4 });
});

test("clicking the lit star clears the rating back to unrated", async ({ page }) => {
  // Clearing has to be reachable: unrated and one star are opposite statements to the engine, so
  // "rate it badly" is not a way back.
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: { ...movieDetail("m1", "Arrival"), userData: aUserData({ userRating: 4 }) } },
  });

  await page.goto("/movies/m1");

  // The fourth star is lit, so its button offers the clear rather than setting the same value again.
  const cleared = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/rating") && request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Clear your rating" }).click();
  await cleared;
});

test("a series is rated as a work", async ({ page }) => {
  // A show gets one verdict; "more like episode 4" is not a question the engine can ask, and there is
  // no episode page to ask it from — the API refuses that case directly.
  await setupApp(page, {
    library: [aSeries("s1", "Severance")],
    detail: { s1: seriesDetail("s1", "Severance") },
  });

  await page.goto("/series/s1");

  const rated = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/s1/rating") && request.method() === "PUT",
  );
  await page.getByRole("button", { name: "Rate 5 stars" }).click();
  expect((await rated).postDataJSON()).toEqual({ rating: 5 });
});

test("logs a watch at a time the user picks, from the overflow menu", async ({ page }) => {
  // The watched toggle claims no time and lands outside the calendar by design; this is the action
  // that dates a viewing the server never observed.
  const logged: Array<{ itemId: string; watchedAt: string }> = [];
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival") },
    logWatch: (itemId, watchedAt) => logged.push({ itemId, watchedAt }),
  });

  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Log watch…" }).click();

  const dialog = page.getByRole("dialog").filter({ hasText: "Log a watch" });
  await dialog.getByLabel("Watched at").fill("2026-07-04T21:15");
  await dialog.getByRole("button", { name: "Log watch" }).click();

  expect(logged).toHaveLength(1);
  expect(logged[0].itemId).toBe("m1");
  // Local wall-clock in, UTC instant out: the calendar buckets by the browser's own day.
  expect(logged[0].watchedAt).toMatch(/Z$/);
});

test("the time field starts fresh on every open, not where it was left", async ({ page }) => {
  // The dialog's content is unmounted while closed, which is what makes "now" mean now on reopening
  // rather than whatever was typed — or whatever the clock said the first time it was built.
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival") },
  });

  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Log watch…" }).click();

  const field = page.getByRole("dialog").getByLabel("Watched at");
  await field.fill("2019-01-05T20:00");
  await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();

  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Log watch…" }).click();

  await expect(page.getByRole("dialog").getByLabel("Watched at")).not.toHaveValue("2019-01-05T20:00");
});

test("a viewer who is not an admin still gets Log watch, and nothing else", async ({ page }) => {
  // The menu used to be admin-only; logging a play against your own history is not an admin act.
  await setupApp(page, {
    role: "user",
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival") },
  });

  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "More actions" }).click();

  await expect(page.getByRole("menuitem", { name: "Log watch…" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Delete…" })).toHaveCount(0);
  await expect(page.getByRole("menuitem", { name: "Move to catalog…" })).toHaveCount(0);
});

test("plays a movie through an Infuse deep link", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival", "329865") },
  });

  // Capture the deep link the page launches (window.open) instead of actually following the custom scheme.
  await page.addInitScript(() => {
    (window as unknown as { __infuse: string[] }).__infuse = [];
    window.open = ((url?: string | URL) => {
      (window as unknown as { __infuse: string[] }).__infuse.push(String(url));
      return null;
    }) as typeof window.open;
  });

  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "Play in Infuse" }).click();

  const opened = await page.evaluate(() => (window as unknown as { __infuse: string[] }).__infuse);
  expect(opened).toContain("infuse://movie/329865?play");
});

test("shows and opens the IMDb movie link", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: { ...movieDetail("m1", "Arrival"), imdbId: "tt2543164" } },
  });

  await page.addInitScript(() => {
    (window as unknown as { __opened: string[] }).__opened = [];
    window.open = ((url?: string | URL) => {
      (window as unknown as { __opened: string[] }).__opened.push(String(url));
      return null;
    }) as typeof window.open;
  });

  await page.goto("/movies/m1");

  const imdb = page.getByRole("button", { name: "View on IMDb" });
  await expect(imdb).toBeVisible();
  await expect(imdb).toContainText("IMDb");

  await imdb.click();
  const opened = await page.evaluate(() => (window as unknown as { __opened: string[] }).__opened);
  expect(opened).toContain("https://www.imdb.com/title/tt2543164/");
});

test("shows movie cast, media, and tags as ordered detail tabs", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: {
      m1: {
        ...movieDetail("m1", "Arrival"),
        mediaSources: [
          {
            id: "source-1",
            versionName: null,
            fileName: "Arrival (2016).mkv",
            container: "mkv",
            sizeBytes: 1024,
            bitrate: null,
            durationTicks: 70_560_000_000,
            streams: [{ type: "Video", index: 0, codec: "h264", language: null, displayTitle: "1080p H.264", title: null }],
          },
        ],
        cast: [{ name: "Amy Adams", character: "Louise Banks", profileUrl: null }],
        studios: [
          { name: "Amazon MGM Studios", logoUrl: null },
          { name: "Pascal Pictures", logoUrl: null },
          { name: "Open Invite Entertainment", logoUrl: null },
        ],
        keywords: ["first contact"],
      },
    },
  });

  await page.goto("/movies/m1");

  const detailTabs = page.getByRole("tab");
  await expect(detailTabs).toHaveCount(3);
  await expect(detailTabs).toHaveText(["Cast", "Media", "Tags"]);
  await expect(page.getByText("Studios: Amazon MGM Studios +2")).toBeVisible();
  await expect(page.getByText("Pascal Pictures")).toHaveCount(0);
  await expect(page.getByText("Amy Adams")).toBeVisible();

  await page.getByRole("tab", { name: "Media" }).click();
  await expect(page.getByText("1080p H.264")).toBeVisible();

  await page.getByRole("tab", { name: /Tags/ }).click();
  await expect(page.getByText("first contact")).toBeVisible();
});

// The movie the merge tests drive: an H264 remux with one embedded track of each kind and two dubs beside
// it, neither carrying a language — which is what makes the language field worth having.
const escapeFromNewYork = () => ({
  ...movieDetail("m1", "Escape from New York"),
  mediaSources: [
    {
      id: "source-1",
      versionName: null,
      fileName: "Escape from New York (1981).mkv",
      container: "mkv",
      sizeBytes: 10_200_547_000,
      bitrate: 13_696_000,
      durationTicks: 59_400_000_000,
      streams: [
        { id: "v0", type: "Video", index: 0, codec: "h264", language: null, displayTitle: "1080p H264", title: null, channels: null, bitrate: 12_000_000, isExternal: false, fileName: null },
        { id: "a0", type: "Audio", index: 1, codec: "dts", language: "eng", displayTitle: "eng DTS 5.1", title: null, channels: 6, bitrate: 1_509_000, isExternal: false, fileName: null },
        { id: "s0", type: "Subtitle", index: 2, codec: "subrip", language: "eng", displayTitle: "eng", title: null, isExternal: false, fileName: null },
        { id: "x1", type: "Audio", index: 1000, codec: null, language: null, displayTitle: null, title: "Гаврилов", isExternal: true, fileName: "Escape from New York (1981).rus.Гаврилов.mka" },
        { id: "x2", type: "Subtitle", index: 1001, codec: null, language: null, displayTitle: null, title: "Сербин", isExternal: true, fileName: "Escape from New York (1981).rus.Сербин.srt" },
      ],
    },
  ],
});

test("merging opens the convert dialog with those tracks checked, instead of starting a job", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: { m1: escapeFromNewYork() },
    transcodeAvailable: true,
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();

  // Checking a sidecar on the Media tab and pressing Merge must not submit anything on its own — the point
  // of the change is that the job is composed first.
  await page.getByRole("checkbox", { name: /Merge .*Гаврилов/ }).check();
  await page.getByRole("button", { name: /Merge 1 into a new version/ }).click();

  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("heading", { name: "Convert version" })).toBeVisible();

  // The hand-off consumes the tab's selection: leaving it checked would leave two places claiming to hold
  // the answer, and the stale one wins the next time the button is pressed.
  await expect(page.getByRole("button", { name: /Merge \d+ into a new version/ })).toHaveCount(0);

  // It arrives checked, and the video defaults to untouched: the operator asked for more tracks, not for a
  // re-encode of a 9.5 GB file.
  await expect(dialog.getByRole("checkbox", { name: /Copy Гаврилов/ })).toBeChecked();
  await expect(dialog.getByRole("checkbox", { name: /Copy Сербин/ })).not.toBeChecked();
  await expect(dialog.getByText("Keep original video — lossless, HDR-safe")).toBeVisible();

  // The other sidecar can still be added here, which is the "довыбрать" half of the request.
  await dialog.getByRole("checkbox", { name: /Copy Сербин/ }).check();

  const submitted = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/transcode") && request.method() === "POST",
  );
  await dialog.getByRole("button", { name: /Start convert \+ merge 2/ }).click();
  const body = JSON.parse((await submitted).postData() ?? "{}");

  expect(body.mergeStreamIds).toEqual(["x1", "x2"]);
  expect(body.videoCodec).toBe("copy");
  // An appended track's name is only addressable when the primary list is explicit, so a merge always sends
  // one — otherwise the engine refuses any edit naming a merged track.
  expect(body.audioStreamIndexes).toEqual([1]);
  expect(body.subtitleStreamIndexes).toEqual([2]);
});

// The same movie with a picture-based subtitle beside its text one — the case extraction has to refuse
// before anything is submitted, rather than after gigabytes have been read.
const withAPictureSubtitle = () => {
  const detail = escapeFromNewYork();
  detail.mediaSources[0].streams.splice(3, 0, {
    id: "s1", type: "Subtitle", index: 3, codec: "hdmv_pgs_subtitle", language: "ger",
    displayTitle: "ger", title: null, isExternal: false, fileName: null,
  } as (typeof detail.mediaSources)[0]["streams"][number]);
  return detail;
};

test("extracting writes the container's own tracks out as files, and refuses the ones no file can hold", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: { m1: withAPictureSubtitle() },
    transcodeAvailable: true,
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Extract tracks to files" }).click();

  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("heading", { name: "Extract tracks to files" })).toBeVisible();

  // The caveat that is true of audio and false of subtitles sits on the audio heading, where the dubs are.
  await expect(dialog.getByText(/only plays once merged back into a video/)).toBeVisible();

  // Audio always becomes Matroska, which carries its own language and title; a text subtitle keeps the
  // format clients read off disk.
  await expect(dialog.getByText(".mka")).toBeVisible();
  await expect(dialog.getByText(".srt")).toBeVisible();

  // A picture-based subtitle is not selectable, and the row says why instead of leaving the operator to
  // discover it from a rejected job.
  const pgs = dialog.getByRole("checkbox", { name: /Extract ger/ });
  await expect(pgs).toBeDisabled();
  await expect(dialog.getByText(/picture-based/)).toBeVisible();

  // A sidecar is already a file: there is nothing to extract it from, so it is not offered here at all.
  await expect(dialog.getByRole("checkbox", { name: /Гаврилов/ })).toHaveCount(0);

  await dialog.getByRole("checkbox", { name: /Extract eng DTS 5.1/ }).check();
  await dialog.getByRole("checkbox", { name: /Extract eng$/ }).check();

  const submitted = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/transcode/extract") && request.method() === "POST",
  );
  await dialog.getByRole("button", { name: /Extract 2 tracks/ }).click();
  const body = JSON.parse((await submitted).postData() ?? "{}");

  expect(body.sourceId).toBe("source-1");
  expect(body.streamIds).toEqual(["a0", "s0"]);
});

test("the quality level reaches every encoder, and an audio track can be re-encoded on its own", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: { m1: escapeFromNewYork() },
    transcodeAvailable: true,
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Convert to a smaller version" }).click();

  const dialog = page.getByRole("dialog");
  // Quality used to appear only for the software encoder, which left no way to ask a GPU for a smaller
  // file. It is now part of every encode, whichever encoder ends up running it.
  await expect(dialog.getByLabel("Quality")).toBeVisible();

  // What a level is worth, in the only unit an operator weighing a day of CPU can act on — the default
  // level first, then the estimate following the level actually selected.
  await expect(dialog.getByText("About 2.5 GB of video, from 8.3 GB.")).toBeVisible();
  await dialog.getByLabel("Quality").click();
  await page.getByRole("option", { name: /Small/ }).click();
  await expect(dialog.getByText("About 850 MB of video, from 8.3 GB.")).toBeVisible();

  // Re-encoding audio is per track and says nothing about the picture.
  await dialog.getByRole("button", { name: "Re-encode" }).click();
  // What the track costs now against what it would cost, plus the downmix — a real loss that is invisible
  // in the output, so the row has to state it before the job starts.
  await expect(dialog.getByText("1.0 GB → E-AC-3, 6 channels, 640 kbps · about 453 MB")).toBeVisible();

  const submitted = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/transcode") && request.method() === "POST",
  );
  await dialog.getByRole("button", { name: /^Start convert$/ }).click();
  const body = JSON.parse((await submitted).postData() ?? "{}");

  expect(body.qualityLevel).toBe("small");
  expect(body.audioTargets).toEqual([{ streamId: "a0", codec: "eac3", bitrate: 640 }]);
  // A level is not a CRF, and the old field is gone from the wire entirely.
  expect(body.crf).toBeUndefined();
});

// The shape the compression controls exist for: a remux whose lossless dubs outweigh the picture, so the
// cheap lever (drop and re-encode tracks) is worth more than the expensive one (re-encode the video).
const aRemuxWithLosslessDubs = () => ({
  ...movieDetail("m1", "Stalker"),
  mediaSources: [
    {
      id: "source-1",
      versionName: null,
      fileName: "Stalker (1979).mkv",
      container: "mkv",
      sizeBytes: 30_000_000_000,
      bitrate: 29_000_000,
      durationTicks: 81_000_000_000,
      streams: [
        { id: "v0", type: "Video", index: 0, codec: "hevc", language: null, displayTitle: "2160p HEVC", title: null, channels: null, bitrate: 12_000_000, isExternal: false, fileName: null },
        ...[1, 2, 3, 4].map((index) => ({
          id: `a${index}`,
          type: "Audio",
          index,
          codec: "dts",
          language: "rus",
          displayTitle: `rus DTS-HD MA 7.1 #${index}`,
          title: null,
          channels: 8,
          bitrate: 4_100_000,
          isExternal: false,
          fileName: null,
        })),
      ],
    },
  ],
});

test("a file whose dubs outweigh its picture says so before the video controls", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Stalker")],
    detail: { m1: aRemuxWithLosslessDubs() },
    transcodeAvailable: true,
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Convert to a smaller version" }).click();

  const dialog = page.getByRole("dialog");
  await expect(
    dialog.getByText(/Audio is the larger half of this file: 15 GB across 4 tracks, against 11 GB of video/),
  ).toBeVisible();

  // 7.1 does not survive E-AC-3, and the row is the only honest place to say so.
  await dialog.getByRole("button", { name: "Re-encode" }).first().click();
  await expect(dialog.getByText("3.9 GB → E-AC-3, 8 channels → 5.1, 640 kbps · about 618 MB").first()).toBeVisible();
});

test("a source that recorded no per-track bitrate shows no estimate rather than a guess", async ({ page }) => {
  // The library was built before the column existed, or by the header reader, which cannot answer this. An
  // overall bitrate is still known — and deliberately not divided up to fill the gap.
  const source = escapeFromNewYork().mediaSources[0];
  const detail = {
    ...escapeFromNewYork(),
    mediaSources: [{ ...source, streams: source.streams.map((stream) => ({ ...stream, bitrate: null })) }],
  };

  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: { m1: detail },
    transcodeAvailable: true,
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Convert to a smaller version" }).click();

  const dialog = page.getByRole("dialog");
  await expect(dialog.getByLabel("Quality")).toBeVisible();
  await expect(dialog.getByText(/of video, from/)).toHaveCount(0);
  await expect(dialog.getByText(/Audio is the larger half/)).toHaveCount(0);

  // The resulting size is still exact — it is the engine's own bitrate times the duration — so the row keeps
  // stating it, and simply leads with the result instead of a comparison it cannot make.
  await dialog.getByRole("button", { name: "Re-encode" }).click();
  await expect(dialog.getByText("→ E-AC-3, 6 channels, 640 kbps · about 453 MB")).toBeVisible();
});

test("a track's language can be corrected, and a tag nobody knows blocks the submit", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: { m1: escapeFromNewYork() },
    transcodeAvailable: true,
    transcodeLanguages: ["eng", "ger", "rus", "ukr"],
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Convert to a smaller version" }).click();

  const dialog = page.getByRole("dialog");
  const language = dialog.getByRole("textbox", { name: /Language for Гаврилов/ });
  await dialog.getByRole("checkbox", { name: /Copy Гаврилов/ }).check();

  // Wrong tags are caught here rather than after the whole form is filled in and submitted — the value is
  // written into the output permanently, and finding out later means re-encoding again.
  await language.fill("rsu");
  await expect(dialog.getByText(/isn’t an ISO 639-2 tag/)).toBeVisible();
  await expect(dialog.getByRole("button", { name: /^Start convert/ })).toBeDisabled();

  await language.fill("rus");
  await expect(dialog.getByText(/isn’t an ISO 639-2 tag/)).toHaveCount(0);

  const submitted = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/transcode") && request.method() === "POST",
  );
  await dialog.getByRole("button", { name: /^Start convert/ }).click();
  const body = JSON.parse((await submitted).postData() ?? "{}");

  // Only what changed travels: the corrected dub, and nothing for the tracks left alone.
  expect(body.metadataEdits).toEqual([{ streamId: "x1", language: "rus" }]);
});

test("the language field accepts every spelling the API does", async ({ page }) => {
  // The served list is the accepted set, not the stored one, and the check drops a BCP-47 subtag the way
  // the service does. Being stricter here than the API would block a submit the server would have taken.
  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: { m1: escapeFromNewYork() },
    transcodeAvailable: true,
    transcodeLanguages: ["de", "deu", "eng", "ger", "por", "pt", "ru", "rus"],
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Convert to a smaller version" }).click();

  const dialog = page.getByRole("dialog");
  const language = dialog.getByRole("textbox", { name: /Language for Гаврилов/ });
  await dialog.getByRole("checkbox", { name: /Copy Гаврилов/ }).check();

  for (const accepted of ["ru", "deu", "pt-BR", "RUS"]) {
    await language.fill(accepted);
    await expect(dialog.getByText(/isn’t an ISO 639-2 tag/)).toHaveCount(0);
    await expect(dialog.getByRole("button", { name: /^Start convert/ })).toBeEnabled();
  }
});

test("dropping a track clears the bad language that was blocking the submit", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: { m1: escapeFromNewYork() },
    transcodeAvailable: true,
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Convert to a smaller version" }).click();

  const dialog = page.getByRole("dialog");
  const sidecar = dialog.getByRole("checkbox", { name: /Copy Гаврилов/ });
  await sidecar.check();
  await dialog.getByRole("textbox", { name: /Language for Гаврилов/ }).fill("rsu");
  await expect(dialog.getByRole("button", { name: /^Start convert/ })).toBeDisabled();

  // The track is no longer in the output, so its value is not going anywhere — the submit has to come back.
  await sidecar.uncheck();
  await expect(dialog.getByRole("button", { name: /^Start convert/ })).toBeEnabled();
});

test("clearing a language means keep, not erase", async ({ page }) => {
  // There is no override that removes a tag, so sending "" would fail the whole submit over a field the
  // operator emptied rather than filled.
  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: { m1: escapeFromNewYork() },
    transcodeAvailable: true,
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();
  await page.getByRole("button", { name: "Convert to a smaller version" }).click();

  const dialog = page.getByRole("dialog");
  // The embedded English track arrives with "eng" already in its field.
  await dialog.getByRole("textbox", { name: /Language for eng DTS 5\.1/ }).fill("");

  const submitted = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/transcode") && request.method() === "POST",
  );
  await dialog.getByRole("button", { name: /^Start convert/ }).click();
  const body = JSON.parse((await submitted).postData() ?? "{}");

  expect(body.metadataEdits).toEqual([]);
});

test("tells sidecar dubs from sidecar subtitles, by kind and by file name", async ({ page }) => {
  // The case that made this necessary: three voice-over dubs and one subtitle, none of them carrying a
  // language or a codec. Every row then reads the same, and nothing on screen says which is which.
  const sidecar = (id: string, type: string, title: string | null, fileName: string, index: number) => ({
    id,
    type,
    index,
    codec: null,
    language: null,
    displayTitle: null,
    title,
    isExternal: true,
    fileName,
  });

  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: {
      m1: {
        ...movieDetail("m1", "Escape from New York"),
        mediaSources: [
          {
            id: "source-1",
            versionName: null,
            fileName: "Escape from New York (1981).mkv",
            container: "mkv",
            sizeBytes: 1024,
            bitrate: null,
            durationTicks: 70_560_000_000,
            streams: [
              { type: "Video", index: 0, codec: "h264", language: null, displayTitle: "1080p H264", title: null, isExternal: false, fileName: null },
              sidecar("x1", "Audio", "Володарский", "Escape from New York (1981).rus.Володарский.mka", 1000),
              sidecar("x2", "Audio", "Гаврилов", "Escape from New York (1981).rus.Гаврилов.mka", 1001),
              sidecar("x3", "Audio", "Горчаков", "Escape from New York (1981).rus.Горчаков.mka", 1002),
              sidecar("x4", "Subtitle", "Сербин", "Escape from New York (1981).rus.Сербин.srt", 1003),
            ],
          },
        ],
      },
    },
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();

  await expect(page.getByText("4 separate files beside this version")).toBeVisible();

  // Each kind heads its own group, and the "merge it first" caveat sits on the audio one — it is false
  // of subtitles, which clients read straight off the disk.
  const audio = page.getByText(/^Audio— only plays once merged into the video$/);
  await expect(audio).toBeVisible();
  await expect(page.getByText("Subtitles", { exact: true })).toBeVisible();

  // The label leads the row rather than trailing a bare "—", and the file name is under it.
  await expect(page.getByText("Гаврилов", { exact: true })).toBeVisible();
  await expect(page.getByText("Escape from New York (1981).rus.Гаврилов.mka")).toBeVisible();
  await expect(page.getByText("Escape from New York (1981).rus.Сербин.srt")).toBeVisible();
  await expect(page.getByText("— “Гаврилов”")).toHaveCount(0);
});

test("a sidecar with specs reads like an embedded track", async ({ page }) => {
  // Once the probe's codec, channels and sample rate are stored, the sidecar row renders through exactly
  // the same DisplayTitle and specs an embedded track does — no separate rendering path.
  await setupApp(page, {
    library: [aMovie("m1", "Escape from New York")],
    detail: {
      m1: {
        ...movieDetail("m1", "Escape from New York"),
        mediaSources: [
          {
            id: "source-1",
            versionName: null,
            fileName: "Escape from New York (1981).mkv",
            container: "mkv",
            sizeBytes: 1024,
            bitrate: null,
            durationTicks: 70_560_000_000,
            streams: [
              { id: "v0", type: "Video", index: 0, codec: "h264", language: null, displayTitle: "1080p H264", title: null, isExternal: false, fileName: null },
              {
                id: "x1",
                type: "Audio",
                index: 1000,
                codec: "ac3",
                language: "rus",
                // The server builds this the same way for both kinds: language + codec + channel label.
                displayTitle: "rus AC3 5.1",
                title: "Гаврилов",
                channels: 6,
                sampleRate: 48000,
                isExternal: true,
                fileName: "Escape from New York (1981).rus.Гаврилов.mka",
              },
            ],
          },
        ],
      },
    },
  });

  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media" }).click();

  // The specs lead and the release name trails in quotes — the shape an embedded track has.
  await expect(page.getByText(/rus AC3 5\.1\s*“Гаврилов”\s*·\s*48 kHz/)).toBeVisible();
});

test("shows series cast, episodes, and tags as ordered detail tabs", async ({ page }) => {
  await setupApp(page, {
    library: [aSeries("s1", "Severance")],
    detail: {
      s1: {
        ...seriesDetail("s1", "Severance", "95396"),
        cast: [{ name: "Adam Scott", character: "Mark Scout", profileUrl: null }],
        keywords: ["workplace"],
      },
    },
    episodes: { s1: [anEpisode("e1", 1, 1, "Good News About Hell")] },
  });

  await page.goto("/series/s1");

  const detailTabs = page.getByRole("tab");
  await expect(detailTabs).toHaveCount(3);
  await expect(detailTabs).toHaveText(["Cast", "Episodes", "Tags"]);
  await expect(page.getByText("Adam Scott")).toBeVisible();

  await page.getByRole("tab", { name: "Episodes" }).click();
  await expect(page.getByText("Season 1")).toBeVisible();
  await expect(page.getByText(/S01E01/)).toBeVisible();
});

test("labels a double-episode file with the range it covers", async ({ page }) => {
  await setupApp(page, {
    library: [aSeries("s1", "Warehouse 13")],
    detail: { s1: seriesDetail("s1", "Warehouse 13", "18164") },
    // One file holds S01E01-E02, so there is no separate item for episode 2 — the row must say so, or the
    // season reads "1, 3" and episode 2 looks lost.
    episodes: {
      s1: [anEpisode("e1", 1, 1, "Pilot", 2), anEpisode("e3", 1, 3, "Magnetism")],
    },
  });

  await page.goto("/series/s1");
  await page.getByRole("tab", { name: "Episodes" }).click();

  // The title stays the first episode's; only the code carries the range.
  await expect(page.getByText("S01E01-E02")).toBeVisible();
  await expect(page.getByText("Pilot")).toBeVisible();
  await expect(page.getByText("S01E03")).toBeVisible();
});

test("admin deletes one episode, keeping the files unless asked", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    library: [aSeries("s1", "Severance")],
    detail: { s1: seriesDetail("s1", "Severance", "95396") },
    episodes: { s1: [anEpisode("e1", 1, 1, "Good News About Hell"), anEpisode("e2", 1, 2, "Half Loop")] },
  });

  await page.goto("/series/s1");
  await page.getByRole("tab", { name: "Episodes" }).click();

  // Keeping the files — and the watch history — is the default: a delete must not silently remove
  // media from disk, and watched state survives as a tombstone unless explicitly purged.
  const kept = page.waitForRequest(
    (request) =>
      request.url().includes("/api/proxy/api/library/episodes/e1?deleteFiles=false&deleteUserData=false") &&
      request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Delete S01E01" }).click();
  await expect(page.getByRole("heading", { name: "Delete episode?" })).toBeVisible();
  await page.getByRole("button", { name: "Remove from library" }).click();
  await kept;
  await expect(page).toHaveURL(/\/series\/s1$/);

  // Ticking the boxes is what escalates to erasing the file and purging the history.
  const erased = page.waitForRequest(
    (request) =>
      request.url().includes("/api/proxy/api/library/episodes/e2?deleteFiles=true&deleteUserData=true") &&
      request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Delete S01E02" }).click();
  await page.getByRole("checkbox", { name: /Delete files from disk/ }).click();
  await page.getByRole("checkbox", { name: /Also delete watch history and favorites/ }).click();
  await page.getByRole("button", { name: "Delete + remove files" }).click();
  await erased;
});

test("admin deletes a whole season and leaves when the series is pruned", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    library: [aSeries("s1", "Severance")],
    detail: { s1: seriesDetail("s1", "Severance", "95396") },
    episodes: { s1: [anEpisode("e1", 1, 1, "Good News About Hell")] },
    // Its only season goes, so nothing is left under the series and the server prunes it too.
    childDelete: { seasonRemoved: true, seriesRemoved: true },
  });

  await page.goto("/series/s1");
  await page.getByRole("tab", { name: "Episodes" }).click();

  const deleted = page.waitForRequest(
    (request) =>
      request.url().includes("/api/proxy/api/library/seasons/season-1?deleteFiles=false&deleteUserData=false") &&
      request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Delete season 1" }).click();
  await expect(page.getByText("1 episode will be removed from the library.")).toBeVisible();
  await page.getByRole("button", { name: "Remove from library" }).click();
  await deleted;

  // The page it was called from no longer exists.
  await expect(page).toHaveURL(/\/series$/);
});

test("a season left with only extras still shows up and stays deletable", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    library: [aSeries("s1", "Severance")],
    detail: {
      // Season 2's episodes are gone but it holds an extra, so the API keeps the season row. Without it in
      // the listing there would be no way to remove that season from the UI at all.
      s1: { ...seriesDetail("s1", "Severance", "95396"), seasons: [aSeason("season-1", 1, 1), aSeason("season-2", 2, 0)] },
    },
    episodes: { s1: [anEpisode("e1", 1, 1, "Good News About Hell")] },
  });

  await page.goto("/series/s1");
  await page.getByRole("tab", { name: "Episodes" }).click();

  await expect(page.getByRole("heading", { name: "Season 2" })).toBeVisible();
  await expect(page.getByText("No episodes in this season.")).toBeVisible();

  const deleted = page.waitForRequest(
    (request) =>
      request.url().includes("/api/proxy/api/library/seasons/season-2?deleteFiles=false") &&
      request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Delete season 2" }).click();
  await page.getByRole("button", { name: "Remove from library" }).click();
  await deleted;
});

test("a non-admin gets no episode or season delete actions", async ({ page }) => {
  await setupApp(page, {
    role: "user",
    library: [aSeries("s1", "Severance")],
    detail: { s1: seriesDetail("s1", "Severance", "95396") },
    episodes: { s1: [anEpisode("e1", 1, 1, "Good News About Hell")] },
  });

  await page.goto("/series/s1");
  await page.getByRole("tab", { name: "Episodes" }).click();

  await expect(page.getByText(/S01E01/)).toBeVisible();
  await expect(page.getByRole("button", { name: "Delete S01E01" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Delete season 1" })).toHaveCount(0);
});

test("admin fixes a misidentified movie and lands on the corrected item", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    library: [aMovie("m1", "Wrong Title")],
    detail: { m1: movieDetail("m1", "Wrong Title"), m2: movieDetail("m2", "Arrival") },
    metadataSearch: [{ reference: { provider: "tmdb", id: "329865" }, title: "Arrival", year: 2016, score: 1 }],
    remapTargetId: "m2",
  });

  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: /Fix match/ }).click();

  await page.getByRole("textbox", { name: "Movie title" }).fill("Arrival");
  await page.getByRole("button", { name: /Search/ }).click();

  const remapped = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/remap") && request.method() === "POST",
  );
  await page.getByRole("button", { name: /Arrival \(2016\)/ }).click();
  await remapped;

  await expect(page).toHaveURL(/\/movies\/m2$/);
  await expect(page.getByRole("heading", { name: "Arrival" })).toBeVisible();
});

test("pins a poster from the detail page when the automatic choice is ambiguous", async ({ page }) => {
  // The point of the control: TMDb's localized poster for a sequel is often the textless international
  // art, so the operator has to be able to say which picture names the film.
  await setupApp(page, {
    library: [aMovie("m1", "John Wick: Chapter 3")],
    detail: { m1: movieDetail("m1", "John Wick: Chapter 3") },
    itemImages: {
      m1: [
        {
          type: "Primary",
          tag: "ru1",
          url: "https://image.tmdb.org/ru.jpg",
          language: "ru",
          sortOrder: 0,
          pinned: false,
          selected: true,
        },
        {
          type: "Primary",
          tag: "en1",
          url: "https://image.tmdb.org/en.jpg",
          language: "en",
          sortOrder: 1,
          pinned: false,
          selected: false,
        },
      ],
    },
  });

  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Choose poster…" }).click();

  await expect(page.getByRole("dialog")).toContainText("Choose a poster");
  const pinned = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/poster") && request.method() === "PUT",
  );
  // The candidates are labelled by the language of their text, which is what the choice is about.
  await page.getByRole("button", { name: /^EN/ }).click();
  expect((await pinned).postDataJSON()).toEqual({ tag: "en1" });
});
