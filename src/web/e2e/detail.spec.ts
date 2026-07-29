import { test, expect } from "@playwright/test";
import { anEpisode, aMovie, aSeason, aSeries, movieDetail, seriesDetail, setupApp } from "./support";

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
        { id: "v0", type: "Video", index: 0, codec: "h264", language: null, displayTitle: "1080p H264", title: null, isExternal: false, fileName: null },
        { id: "a0", type: "Audio", index: 1, codec: "dts", language: "eng", displayTitle: "eng DTS 5.1", title: null, isExternal: false, fileName: null },
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
