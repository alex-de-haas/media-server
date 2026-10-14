import { test, expect } from "@playwright/test";
import { anEpisode, aMovie, aSeason, aSeries, aTranscodeJob, aUserData, episodeDetail, managementPage, managementRow, movieDetail, seriesDetail, setupApp } from "./support";

test("opens a movie detail page and records a dated watch", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival") },
  });

  await page.goto("/movies");
  await page.getByRole("link", { name: /Arrival/ }).click();
  await expect(page).toHaveURL(/\/movies\/m1$/);
  await expect(page.getByRole("heading", { name: "Arrival" })).toBeVisible();

  const played = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/watches") && request.method() === "POST",
  );
  await expect(page.getByRole("button", { name: "Log watch", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Log watch…" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Log watch", exact: true }).click();
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

test("a viewer who is not an admin gets personal actions without administration", async ({ page }) => {
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

test("movie details offer library controls without playback launch or copy links", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival", "329865") },
  });
  await page.goto("/movies/m1");
  await expect(page.getByRole("heading", { name: "Arrival" })).toBeVisible();
  await expect(page.getByRole("button", { name: /Play in Infuse|Open in Infuse|Copy Infuse link/ })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Add favorite" })).toBeVisible();
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

  await expect(page.getByRole("button", { name: "View on IMDb" })).toHaveCount(0);
  await page.getByRole("button", { name: "More actions" }).click();
  const imdb = page.getByRole("menuitem", { name: "View on IMDb" });
  await expect(imdb).toBeVisible();
  await expect(imdb).toContainText("IMDb");

  await imdb.click();
  const opened = await page.evaluate(() => (window as unknown as { __opened: string[] }).__opened);
  expect(opened).toContain("https://www.imdb.com/title/tt2543164/");
});

for (const kind of ["Movie", "Series"] as const) {
  test(`${kind} viewers can open tracking and the correct TMDb page from More actions`, async ({ page }) => {
    const detail = kind === "Movie" ? movieDetail("item", "Arrival", "329865") : seriesDetail("item", "Severance", "95396");
    await setupApp(page, { role: "user", detail: { item: detail } });
    await page.addInitScript(() => {
      (window as unknown as { __opened: string[] }).__opened = [];
      window.open = ((url?: string | URL) => {
        (window as unknown as { __opened: string[] }).__opened.push(String(url));
        return null;
      }) as typeof window.open;
    });
    await page.goto(`/${kind === "Movie" ? "movies" : "series"}/item`);
    await expect(page.getByRole("button", { name: "Track / remind me", exact: true })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Log watch", exact: true })).toHaveCount(0);
    await page.getByRole("button", { name: "More actions" }).click();
    await expect(page.getByRole("menuitem", { name: "View on IMDb" })).toHaveCount(0);
    await expect(page.getByRole("menuitem", { name: "Refresh metadata" })).toHaveCount(0);
    await page.getByRole("menuitem", { name: "Track / remind me" }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText(detail.title);
    await expect(dialog.getByRole("button", { name: "Set reminder" })).toBeVisible();
    await dialog.getByRole("button", { name: "Close", exact: true }).click();
    await page.getByRole("button", { name: "More actions" }).click();
    await page.getByRole("menuitem", { name: "View on TMDb" }).click();
    const opened = await page.evaluate(() => (window as unknown as { __opened: string[] }).__opened);
    expect(opened).toContain(`https://www.themoviedb.org/${kind === "Movie" ? "movie/329865" : "tv/95396"}`);
  });
}

test("a movie without provider IDs omits tracking and provider links", async ({ page }) => {
  await setupApp(page, { role: "user", detail: { m1: movieDetail("m1", "Arrival") } });
  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "More actions" }).click();
  await expect(page.getByRole("menuitem")).toHaveText(["Log watch…"]);
});

test("keeps movie cast in a carousel above media and tags", async ({ page }, testInfo) => {
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
        cast: [
          { provider: "tmdb", providerId: "9273", name: "Amy Adams", character: "Louise Banks", profileUrl: null },
          ...Array.from({ length: 8 }, (_, index) => ({
            provider: "tmdb", providerId: `cast-${index}`, name: `Cast member ${index + 2}`, character: `Role ${index + 2}`, profileUrl: null,
          })),
        ],
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

  await expect(page.getByRole("tab")).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Media", exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Tags", exact: true })).toBeVisible();
  await expect(page.locator("main h2")).toHaveText(["Cast", "Media", "Tags"]);
  await expect(page.getByText("Studios: Amazon MGM Studios +2")).toBeVisible();
  await expect(page.getByText("Pascal Pictures")).toHaveCount(0);
  await expect(page.getByText("Amy Adams")).toBeVisible();
  const cast = page.getByRole("region", { name: "Cast", exact: true });
  const amy = cast.getByRole("link", { name: /Amy Adams/ });
  await expect(amy).toHaveAttribute("href", "/people/tmdb-9273");
  const previous = cast.getByRole("button", { name: "Previous cast members" });
  const next = cast.getByRole("button", { name: "Next cast members" });
  await expect(previous).toBeDisabled();
  await next.click();
  await expect(previous).toBeEnabled();
  await previous.focus();
  await page.keyboard.press("ArrowLeft");
  await expect(previous).toBeDisabled();
  await page.screenshot({ path: testInfo.outputPath("cast-carousel-desktop.png"), fullPage: true });

  await expect(page.getByText("1080p H.264")).toBeVisible();

  await expect(page.getByText("first contact")).toBeVisible();
  await expect(cast).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  const lastActor = cast.getByRole("link", { name: /Cast member 9/ });
  await lastActor.focus();
  await expect(lastActor).toBeInViewport();
  await page.screenshot({ path: testInfo.outputPath("cast-carousel-mobile.png"), fullPage: true });
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

test("containers disclose their own track rows while separate files remain visible and selected", async ({ page }) => {
  const detail = escapeFromNewYork();
  const source = detail.mediaSources[0];
  const embedded = source.streams.filter(stream => !stream.isExternal).map(stream => ({ ...stream, isForced: stream.type === "Subtitle" }));
  await setupApp(page, {
    library: [aMovie("m1", detail.title)],
    detail: { m1: { ...detail, mediaSources: [
      { ...source, streams: [...embedded, ...source.streams.filter(stream => stream.isExternal)] },
      { ...source, id: "source-2", fileName: "Escape from New York (1981) - Copy.mkv", streams: embedded },
    ] } },
    management: () => managementPage([managementRow("m1", detail.title, { versionCount: 2 })]),
    transcodeAvailable: true,
  });
  await page.goto("/manage/movies");
  await page.getByRole("button", { name: `Show versions of ${detail.title}` }).click();
  const version = page.locator("#source-source-1");
  const copy = page.locator("#source-source-2");
  const sidecar = version.getByRole("checkbox", { name: /Select .*Гаврилов/ });
  await expect(version.getByRole("cell", { name: "Container", exact: true })).toBeVisible();
  await expect(version.getByText("Embedded", { exact: true })).toHaveCount(0);
  await sidecar.check();

  const expand = version.getByRole("button", { name: `Show tracks in ${source.fileName}` });
  await expand.focus();
  await page.keyboard.press("Enter");
  await expect(version.getByRole("button", { name: `Hide tracks in ${source.fileName}` })).toHaveAttribute("aria-expanded", "true");
  const tracks = version.getByRole("row").filter({ hasText: "Embedded" });
  await expect(tracks).toHaveCount(3);
  await expect(tracks.getByRole("checkbox")).toHaveCount(3);
  await expect(tracks.getByRole("button")).toHaveCount(3);
  const audio = tracks.filter({ hasText: "eng DTS 5.1" });
  await expect(audio.getByText("Default", { exact: true })).toBeVisible();
  await expect(audio.getByRole("cell", { name: "Audio", exact: true })).toBeVisible();
  await expect(audio.getByText("1,509 kbps", { exact: true })).toBeVisible();
  const subtitles = tracks.filter({ hasText: "Subtitles" });
  await expect(subtitles.getByText("Forced", { exact: true })).toBeVisible();
  await expect(subtitles.getByRole("cell", { name: /SUBRIP/ })).toBeVisible();
  await expect(subtitles.getByRole("cell", { name: "—", exact: true })).toBeVisible();
  await expect(copy.getByText("Embedded", { exact: true })).toHaveCount(0);
  await expect(sidecar).toBeChecked();
  await version.getByRole("checkbox", { name: /Select Video track 1/ }).check();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);

  await version.getByRole("button", { name: `Hide tracks in ${source.fileName}` }).focus();
  await page.keyboard.press("Space");
  await expect(expand).toHaveAttribute("aria-expanded", "false");
  await expect(version.getByText("Embedded", { exact: true })).toHaveCount(0);
  await expect(sidecar).toBeChecked();
  await expect(page.getByText("2 selected", { exact: true })).toBeVisible();
  await copy.getByRole("button", { name: /Show tracks in/ }).click();
  for (const checkbox of await copy.getByRole("checkbox").all()) await expect(checkbox).toBeDisabled();
  await page.getByRole("button", { name: "Clear selection" }).click();
  for (const checkbox of await copy.getByRole("checkbox").all()) await expect(checkbox).toBeEnabled();
});

async function openTrackGrid(page: import("@playwright/test").Page) {
  await page.goto("/manage/movies/m1");
  await page.getByRole("button", { name: /Show tracks in Escape/ }).click();
  await selectMovieTracks(page);
}

for (const operation of ["Remux", "Convert"] as const) {
  test(`${operation} combines a container with external audio and subtitles from another version`, async ({ page }) => {
    const detail = escapeFromNewYork();
    const original = detail.mediaSources[0];
    const copy = { ...original, id: "source-2", fileName: "Copy.mkv", streams: original.streams.filter(stream => !stream.isExternal).map(stream => ({ ...stream, id: `copy-${stream.id}` })) };
    await setupApp(page, { detail: { m1: { ...detail, mediaSources: [copy, original] } }, transcodeAvailable: true });
    await page.goto("/manage/movies/m1");
    const originalGrid = page.locator("#source-source-1");
    const copyGrid = page.locator("#source-source-2");
    const externalAudio = originalGrid.getByRole("checkbox", { name: /Select .*Гаврилов/ });
    const externalSubtitle = originalGrid.getByRole("checkbox", { name: /Select .*srt/ });
    if (operation === "Remux") await externalAudio.check();
    await copyGrid.getByRole("button", { name: "Show tracks in Copy.mkv" }).click();
    await copyGrid.getByRole("checkbox", { name: /Select Video track 1/ }).check();
    await copyGrid.getByRole("checkbox", { name: /Select Audio track 2/ }).check();
    await externalAudio.check();
    await externalSubtitle.check();
    await page.getByRole("button", { name: operation, exact: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("textbox", { name: /Name of .*Гаврилов/ })).toBeVisible();
    await expect(dialog.getByRole("textbox", { name: /Name of .*srt/ })).toBeVisible();
    const submitted = page.waitForRequest(request => request.url().endsWith("/transcode") && request.method() === "POST");
    await dialog.getByRole("button", { name: `Start ${operation.toLowerCase()}` }).click();
    expect((await submitted).postDataJSON()).toMatchObject({ sourceId: "source-2", videoStreamIndex: 0,
      audioStreamIndexes: [1], subtitleStreamIndexes: [], mergeStreamIds: ["x1", "x2"] });
    await expect(dialog).not.toBeVisible();
  });
}

async function selectMovieTracks(page: import("@playwright/test").Page) {
  await page.getByRole("checkbox", { name: /Select Video track 1/ }).check();
  await page.getByRole("checkbox", { name: /Select Audio track 2/ }).check();
  await page.getByRole("checkbox", { name: /Select Subtitle track 3/ }).check();
}

test("remux confirms the grid selection, names video and sidecars, and never picks tracks again", async ({ page }, testInfo) => {
  await setupApp(page, { library: [aMovie("m1", "Escape from New York")], detail: { m1: escapeFromNewYork() }, transcodeAvailable: true });
  await openTrackGrid(page);
  await page.getByRole("checkbox", { name: /Select Subtitle track 3/ }).uncheck();
  await page.getByRole("checkbox", { name: /Select .*Гаврилов/ }).check();
  await page.getByRole("button", { name: "Remux", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("heading", { name: "Remux selected tracks" })).toBeVisible();
  await expect(dialog.getByRole("combobox")).toHaveCount(1);
  await expect(dialog.getByRole("combobox", { name: "Output destination" })).toBeVisible();
  await expect(dialog.getByRole("checkbox")).toHaveCount(0);
  await expect(dialog.getByRole("textbox", { name: /Language/ })).toHaveCount(0);
  await expect(dialog.getByRole("textbox")).toHaveCount(4);
  await expect(dialog.getByRole("textbox", { name: "Version name", exact: true })).toHaveValue("Remux");
  await dialog.getByRole("textbox", { name: "Version name", exact: true }).fill("Cinema cut");
  await dialog.getByRole("textbox", { name: "Name of Video track 1" }).fill("Cinema picture");
  await dialog.getByRole("textbox", { name: /Name of .*Гаврилов/ }).fill("Dub");
  await page.screenshot({ path: testInfo.outputPath("fixed-remux.png"), fullPage: false });
  const submitted = page.waitForRequest(request => request.url().endsWith("/transcode") && request.method() === "POST");
  await dialog.getByRole("button", { name: "Start remux" }).click();
  expect((await submitted).postDataJSON()).toMatchObject({ versionName: "Cinema cut", videoCodec: "copy", videoStreamIndex: 0, audioStreamIndexes: [1], subtitleStreamIndexes: [], mergeStreamIds: ["x1"], metadataEdits: [{ streamId: "v0", title: "Cinema picture" }, { streamId: "x1", title: "Dub" }] });
  await expect(dialog).not.toBeVisible();
  await expect(page.getByRole("button", { name: "Clear selection" })).toHaveCount(0);
});

test("single and batch extraction use the exact grid tracks, with unsupported formats disabled", async ({ page }) => {
  const detail = escapeFromNewYork();
  detail.mediaSources[0].streams.push({ id: "pgs", type: "Subtitle", index: 3, codec: "hdmv_pgs_subtitle", language: "eng", displayTitle: "eng", title: null, isExternal: false, fileName: null });
  await setupApp(page, { library: [aMovie("m1", detail.title)], detail: { m1: detail }, transcodeAvailable: true });
  await openTrackGrid(page);
  await expect(page.getByRole("button", { name: "Extract Video track 1", exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Extract Subtitle track 4", exact: true })).toBeDisabled();
  let submitted = page.waitForRequest(request => request.url().endsWith("/transcode/extract") && request.method() === "POST");
  await page.getByRole("button", { name: "Extract Audio track 2", exact: true }).click();
  expect((await submitted).postDataJSON()).toEqual({ sourceId: "source-1", streamIds: ["a0"] });
  await expect(page.getByRole("button", { name: "Clear selection" })).toHaveCount(0);
  await page.getByRole("checkbox", { name: /Select Audio track 2/ }).check();
  await page.getByRole("checkbox", { name: /Select Subtitle track 3/ }).check();
  submitted = page.waitForRequest(request => request.url().endsWith("/transcode/extract") && request.method() === "POST");
  await page.getByRole("button", { name: "Extract", exact: true }).click();
  expect((await submitted).postDataJSON()).toEqual({ sourceId: "source-1", streamIds: ["a0", "s0"] });
  await expect(page.getByRole("dialog")).toHaveCount(0);
});

test("Convert exposes encoding controls and selected audio settings; cancelled operations start fresh", async ({ page }, testInfo) => {
  await setupApp(page, { library: [aMovie("m1", "Escape from New York")], detail: { m1: escapeFromNewYork() }, transcodeAvailable: true });
  await openTrackGrid(page);
  await page.getByRole("checkbox", { name: /Select Subtitle track 3/ }).uncheck();
  await page.getByRole("button", { name: "Convert", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await page.screenshot({ path: testInfo.outputPath("fixed-convert.png"), fullPage: false });
  await expect(dialog.getByRole("combobox")).toHaveCount(5);
  await expect(dialog.getByRole("combobox", { name: "Output destination" })).toBeVisible();
  await expect(dialog.getByRole("textbox", { name: "Version name", exact: true })).toHaveValue("Convert");
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.setViewportSize({ width: 1280, height: 720 });
  await expect(dialog.getByText(/approximately/)).toBeVisible();
  await dialog.getByLabel("Quality", { exact: true }).click();
  await page.getByRole("option", { name: /Small/ }).click();
  await dialog.getByRole("checkbox", { name: /Re-encode Audio track 2/ }).check();
  await expect(dialog.getByText(/6 channels, 640 kbps/)).toBeVisible();
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByText("2 selected", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Remux", exact: true }).click();
  await expect(dialog.getByRole("checkbox")).toHaveCount(0);
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "Convert", exact: true }).click();
  await expect(dialog.getByLabel("Quality", { exact: true })).toContainText("High — recommended");
  await expect(dialog.getByRole("checkbox", { name: /Re-encode Audio track 2/ })).not.toBeChecked();
  await dialog.getByLabel("Codec", { exact: true }).click();
  await page.getByRole("option", { name: /H.264 —/ }).click();
  await dialog.getByLabel("Resolution", { exact: true }).click();
  await page.getByRole("option", { name: "720p (HD)", exact: true }).click();
  await dialog.getByRole("checkbox", { name: /Re-encode Audio track 2/ }).check();
  const submitted = page.waitForRequest(request => request.url().endsWith("/transcode") && request.method() === "POST");
  await dialog.getByRole("button", { name: "Start convert" }).click();
  expect((await submitted).postDataJSON()).toMatchObject({ versionName: "Convert", videoCodec: "h264", videoStreamIndex: 0, maxHeight: 720, qualityLevel: "high", audioTargets: [{ streamId: "a0", codec: "eac3", bitrate: 640 }] });
});

for (const available of [true, false]) {
  test(`old or unavailable engine explains blocked composition (${available})`, async ({ page }) => {
    await setupApp(page, { library: [aMovie("m1", "Escape from New York")], detail: { m1: escapeFromNewYork() }, transcodeAvailable: available, explicitVideoSelection: false });
    await page.goto("/manage/movies/m1");
    await page.getByRole("button", { name: /Show tracks in Escape/ }).click();
    await page.getByRole("checkbox", { name: /Select Video track 1/ }).check();
    await page.getByRole("checkbox", { name: /Select Audio track 2/ }).check();
    await expect(page.getByRole("button", { name: "Remux", exact: true })).toBeDisabled();
    await expect(page.getByText(available ? /Update Transcode Engine/ : /Transcode Engine is unavailable/, { exact: false })).toBeVisible();
    await expect(page.getByRole("button", { name: "Extract Audio track 2", exact: true })).toBeEnabled({ enabled: available });
  });
}

for (const tooling of [true, false]) {
  test(`Dolby Vision rewrite is Remux-only and tooling gated (${tooling})`, async ({ page }) => {
    const detail = escapeFromNewYork();
    Object.assign(detail.mediaSources[0].streams[0], { hdrFormat: "Dolby Vision", dolbyVision: { profile: 7, level: 6, compatibilityId: 6, rpuPresent: true, elPresent: true, blPresent: true } });
    await setupApp(page, { library: [aMovie("m1", detail.title)], detail: { m1: detail }, transcodeAvailable: true, dolbyVisionConversion: tooling });
    await openTrackGrid(page);
    await page.getByRole("button", { name: "Remux", exact: true }).click();
    const dialog = page.getByRole("dialog");
    const rewrite = dialog.getByRole("checkbox", { name: "Convert Dolby Vision to profile 8.1" });
    await expect(rewrite).not.toBeChecked();
    await expect(rewrite).toBeEnabled({ enabled: tooling });
    if (tooling) {
      await rewrite.check();
      const submitted = page.waitForRequest(request => request.url().endsWith("/transcode") && request.method() === "POST");
      await dialog.getByRole("button", { name: "Start remux" }).click();
      expect((await submitted).postDataJSON().dolbyVision).toBe("toProfile81");
      await expect(dialog).not.toBeVisible();
      await selectMovieTracks(page);
    } else await page.keyboard.press("Escape");
    await page.getByRole("button", { name: "Convert", exact: true }).click();
    await expect(dialog.getByRole("checkbox", { name: "Convert Dolby Vision to profile 8.1" })).toHaveCount(0);
    await expect(dialog.getByText(/HDR10/)).toBeVisible();
  });
}

test("version name validation preserves selection and track names, then retries with a new name", async ({ page }, testInfo) => {
  await setupApp(page, { library: [aMovie("m1", "Escape from New York")], detail: { m1: escapeFromNewYork() }, transcodeAvailable: true });
  let attempts = 0;
  await page.route("**/api/proxy/api/transcode", route => {
    if (route.request().method() !== "POST") return route.fallback();
    attempts++;
    return attempts === 1 ? route.fulfill({ status: 400, json: { error: "invalid_version_name", detail: "A version with this name already exists. Choose another name." } }) : route.fallback();
  });
  await openTrackGrid(page);
  await page.getByRole("button", { name: "Remux", exact: true }).click();
  const dialog = page.getByRole("dialog");
  const name = dialog.getByRole("textbox", { name: "Version name", exact: true });
  await name.fill("../escape");
  await expect(dialog.getByRole("button", { name: "Start remux" })).toBeDisabled();
  await name.fill("");
  await expect(dialog.getByRole("alert")).toContainText("Enter a version name");
  await name.fill("Remux");
  await dialog.getByRole("textbox", { name: "Name of Video track 1" }).fill("Keep this name");
  await dialog.getByRole("button", { name: "Start remux" }).click();
  await expect(dialog.getByRole("alert")).toContainText("already exists");
  await expect(name).toHaveAttribute("aria-invalid", "true");
  await expect(name).toBeFocused();
  await expect(dialog.getByRole("button", { name: "Start remux" })).toBeDisabled();
  await page.screenshot({ path: testInfo.outputPath("version-name-conflict.png"), fullPage: false });
  await expect(dialog.getByRole("textbox", { name: "Name of Video track 1" })).toHaveValue("Keep this name");
  await name.fill("My cut");
  await expect(dialog.getByRole("alert")).toHaveCount(0);
  const submitted = page.waitForRequest(request => request.url().endsWith("/transcode") && request.method() === "POST");
  await dialog.getByRole("button", { name: "Start remux" }).click();
  expect((await submitted).postDataJSON()).toMatchObject({ versionName: "My cut", metadataEdits: [{ streamId: "v0", title: "Keep this name" }] });
  await expect(dialog).not.toBeVisible();
  expect(attempts).toBe(2);
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

  await page.goto("/manage/movies/m1");

  const files = page.locator("#source-source-1").getByRole("row").filter({ hasText: "Separate file" });
  await expect(files).toHaveCount(4);
  // Kind and origin live in the same columns as embedded tracks. Only external audio needs merging.
  const audio = files.filter({ hasText: "Гаврилов.mka" });
  await expect(audio.getByRole("cell", { name: "Audio", exact: true })).toBeVisible();
  await expect(audio.getByText("Merge for playback")).toBeVisible();
  const subtitles = files.filter({ hasText: "Сербин.srt" });
  await expect(subtitles.getByRole("cell", { name: "Subtitles", exact: true })).toBeVisible();
  await expect(subtitles.getByText("Merge for playback")).toHaveCount(0);

  // An unprobed file still has its release label and full filename, without a leading empty value.
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

  await page.goto("/manage/movies/m1");

  // The specs lead and the release name trails in quotes — the shape an embedded track has.
  await expect(page.getByText(/rus AC3 5\.1\s*“Гаврилов”\s*·\s*48 kHz/)).toBeVisible();
});

test("keeps series cast in a carousel above episodes and tags", async ({ page }) => {
  await setupApp(page, {
    library: [aSeries("s1", "Severance")],
    detail: {
      s1: {
        ...seriesDetail("s1", "Severance", "95396"),
        cast: [{ provider: "tmdb", providerId: "119", name: "Adam Scott", character: "Mark Scout", profileUrl: null }],
        keywords: ["workplace"],
      },
    },
    episodes: { s1: [anEpisode("e1", 1, 1, "Good News About Hell")] },
  });

  await page.goto("/series/s1");
  await page.getByRole("button", { name: /^Season 1 ·/ }).click();

  await expect(page.getByRole("tab")).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Episodes", exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Tags", exact: true })).toBeVisible();
  await expect(page.locator("main h2")).toHaveText(["Cast", "Episodes", "Tags"]);
  await expect(page.getByText("Adam Scott")).toBeVisible();
  await expect(page.getByRole("region", { name: "Cast", exact: true }).getByRole("button")).toHaveCount(0);

  await expect(page.getByText("Season 1")).toBeVisible();
  await expect(page.getByText(/S01E01/)).toBeVisible();
});

test("library seasons start collapsed and expand independently with keyboard support", async ({ page }) => {
  await setupApp(page, {
    library: [aSeries("s1", "Severance")],
    detail: {
      s1: { ...seriesDetail("s1", "Severance"), seasons: [aSeason("season-1", 1, 1), aSeason("season-3", 3, 1)] },
    },
    episodes: { s1: [anEpisode("e1", 1, 1, "First season episode"), anEpisode("e3", 3, 1, "Third season episode")] },
  });
  await page.goto("/series/s1");
  const first = page.getByRole("button", { name: /^Season 1 ·/ });
  const third = page.getByRole("button", { name: /^Season 3 ·/ });
  await expect(first).toHaveAttribute("aria-expanded", "false");
  await expect(first).toHaveText("Season 1 · 1 episode");
  await expect(third).toHaveAttribute("aria-expanded", "false");
  await expect(page.getByRole("button", { name: /^Season 2 ·/ })).toHaveCount(0);
  await expect(page.getByRole("listitem").filter({ hasText: "First season episode" })).not.toBeVisible();
  await expect(page.getByRole("listitem").filter({ hasText: "Third season episode" })).not.toBeVisible();
  await first.press("Enter");
  await expect(page.getByRole("listitem").filter({ hasText: "First season episode" })).toBeVisible();
  await third.click();
  await expect(first).toHaveAttribute("aria-expanded", "true");
  await expect(page.getByRole("listitem").filter({ hasText: "Third season episode" })).toBeVisible();
  await first.press("Space");
  await expect(first).toHaveAttribute("aria-expanded", "false");
  await expect(page.getByRole("listitem").filter({ hasText: "First season episode" })).not.toBeVisible();
  await expect(third).toHaveAttribute("aria-expanded", "true");
  await page.reload();
  await expect(first).toHaveAttribute("aria-expanded", "false");
  await expect(third).toHaveAttribute("aria-expanded", "false");
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
  await page.getByRole("button", { name: /^Season 1 ·/ }).click();

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

  await page.goto("/manage/series/s1");

  // Keeping the files — and the watch history — is the default: a delete must not silently remove
  // media from disk, and watched state survives as a tombstone unless explicitly purged.
  const kept = page.waitForRequest(
    (request) =>
      request.url().includes("/api/proxy/api/library/episodes/e1?deleteFiles=false&deleteUserData=false") &&
      request.method() === "DELETE",
  );
  await page.getByRole("row").filter({ hasText: "S01E01" }).getByRole("button", { name: "More actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Delete…", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Delete episode?" })).toBeVisible();
  await page.getByRole("button", { name: "Remove from library" }).click();
  await kept;
  await expect(page).toHaveURL(/\/manage\/series\/s1$/);

  // Ticking the boxes is what escalates to erasing the file and purging the history.
  const erased = page.waitForRequest(
    (request) =>
      request.url().includes("/api/proxy/api/library/episodes/e2?deleteFiles=true&deleteUserData=true") &&
      request.method() === "DELETE",
  );
  await page.getByRole("row").filter({ hasText: "S01E02" }).getByRole("button", { name: "More actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Delete…", exact: true }).click();
  await page.getByRole("checkbox", { name: /Delete files from disk/ }).click();
  await page.getByRole("checkbox", { name: /Also delete watch history and favorites/ }).click();
  await page.getByRole("button", { name: "Delete + remove files" }).click();
  await erased;
});

test("admin deletes a whole season and leaves when the series is pruned", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    library: [aSeries("s1", "Severance")],
    detail: { s1: { ...seriesDetail("s1", "Severance", "95396"), seasons: [aSeason("season-1", 1, 1)] } },
    episodes: { s1: [anEpisode("e1", 1, 1, "Good News About Hell")] },
    // Its only season goes, so nothing is left under the series and the server prunes it too.
    childDelete: { seasonRemoved: true, seriesRemoved: true },
  });

  await page.goto("/manage/series/s1");

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
  await expect(page).toHaveURL(/\/manage\/series$/);
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

  await page.goto("/manage/series/s1");
  await page.getByRole("button", { name: "Season 2", exact: true }).click();

  await expect(page).toHaveURL(/season=2/);
  await expect(page.getByText("No titles match these filters.")).toBeVisible();

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
  await page.getByRole("button", { name: /^Season 1 ·/ }).click();

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

  await page.goto("/manage/movies/m1");
  await page.getByRole("row").filter({ has: page.getByText("Wrong Title", { exact: true }) }).getByRole("button", { name: "More actions", exact: true }).click();
  await page.getByRole("menuitem", { name: /Fix match/ }).click();

  await page.getByRole("textbox", { name: "Movie title" }).fill("Arrival");
  await page.getByRole("button", { name: /Search/ }).click();

  const remapped = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/remap") && request.method() === "POST",
  );
  await page.getByRole("button", { name: /Arrival \(2016\)/ }).click();
  await remapped;

  await expect(page).toHaveURL(url => url.pathname === "/manage/movies" && url.searchParams.get("expanded") === "m2" && url.searchParams.has("return") && !url.searchParams.has("focus"));
  await expect(page.getByRole("button", { name: "Hide versions of Arrival" })).toBeVisible();
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

  await page.goto("/manage/movies/m1");
  await page.getByRole("button", { name: "More actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Choose poster…" }).click();

  await expect(page.getByRole("dialog")).toContainText("Choose a poster");
  const pinned = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/poster") && request.method() === "PUT",
  );
  // The candidates are labelled by the language of their text, which is what the choice is about.
  await page.getByRole("button", { name: /^EN/ }).click();
  expect((await pinned).postDataJSON()).toEqual({ tag: "en1" });
});

// ── Episodes: the media surface a movie has, on every row ────────────────────────────────────────

// Two versions of the pilot, as `GET /library/e1` answers once the row is opened.
const pilotDetail = () => ({
  ...episodeDetail("e1", "Good News About Hell"),
  mediaSources: [
    {
      id: "source-1",
      versionName: null,
      fileName: "Severance S01E01.mkv",
      container: "mkv",
      sizeBytes: 4_100_000_000,
      bitrate: null,
      durationTicks: 33_600_000_000,
      streams: [
        { id: "v0", type: "Video", index: 0, codec: "hevc", language: null, displayTitle: "2160p HEVC", title: null, isExternal: false, fileName: null },
        { id: "a0", type: "Audio", index: 1, codec: "eac3", language: "eng", displayTitle: "eng EAC3 5.1", title: null, channels: 6, isExternal: false, fileName: null },
      ],
    },
    {
      id: "source-2",
      versionName: "HEVC 1080p",
      fileName: "Severance S01E01 - HEVC 1080p.mkv",
      container: "mkv",
      sizeBytes: 900_000_000,
      bitrate: null,
      durationTicks: 33_600_000_000,
      streams: [{ id: "v1", type: "Video", index: 0, codec: "hevc", language: null, displayTitle: "1080p HEVC", title: null, isExternal: false, fileName: null }],
    },
  ],
});

// What the listing says about the pilot without anyone opening it: the default version's picture and size.
const pilotSummary = {
  versionCount: 2,
  videoCodec: "hevc",
  height: 2160,
  hdrFormat: "Dolby Vision · HDR10",
  dolbyVision: { profile: 7, level: 6, blCompatibilityId: 6, enhancementLayer: true },
  sizeBytes: 4_100_000_000,
};

test("an episode row shows its still, air date and synopsis, and a placeholder without a still", async ({ page }) => {
  await setupApp(page, {
    role: "user",
    library: [aSeries("s1", "Severance")],
    detail: { s1: seriesDetail("s1", "Severance", "95396") },
    episodes: {
      s1: [
        anEpisode("e1", 1, 1, "Good News About Hell", null, {
          stillUrl: "https://image.tmdb.org/t/p/original/still.jpg",
          airDate: "2022-02-17T00:00:00+00:00",
          overview: "Mark is promoted to lead the team after his friend leaves the company.",
          userData: aUserData({ played: true, playCount: 1 }),
        }),
        anEpisode("e2", 1, 2, "Half Loop"),
      ],
    },
  });

  await page.goto("/series/s1");
  await page.getByRole("button", { name: /^Season 1 ·/ }).click();

  // The still is the provider's frame for this episode, the air date its calendar day (not shifted by
  // the viewer's zone), and the synopsis sits under them; the runtime moved onto the same fact line.
  const pilot = page.getByRole("listitem").filter({ hasText: "S01E01" });
  await expect(pilot.locator("img")).toHaveAttribute("src", "https://image.tmdb.org/t/p/original/still.jpg");
  await expect(pilot.getByText("Feb 17, 2022 · 4m")).toBeVisible();
  await expect(pilot.getByText("Mark is promoted to lead the team after his friend leaves the company.")).toBeVisible();

  // A watched episode wears the poster card's check badge on its still, and the toggle sits among the
  // row's actions rather than beside the still.
  // Exact: "Mark unwatched" would otherwise match the badge's label too.
  await expect(pilot.getByLabel("Watched", { exact: true })).toBeVisible();
  await expect(pilot.getByRole("button", { name: "Mark unwatched" })).toHaveCount(0);
  await expect(pilot.getByRole("button", { name: "Log watch" })).toBeVisible();

  // A row without a still says so rather than borrowing the show's backdrop.
  const second = page.getByRole("listitem").filter({ hasText: "S01E02" });
  await expect(second.locator("img")).toHaveCount(0);
  await expect(second.getByText("No preview")).toBeVisible();
  await expect(second.getByText("4m")).toBeVisible();
  await expect(second.getByLabel("Watched", { exact: true })).toHaveCount(0);
  await expect(second.getByLabel("Watch status")).toHaveText("Not watched");
});

test("an episode row keeps its title readable on a phone by dropping the actions under the facts", async ({ page }) => {
  // The worst case: an admin (every control) with a still and a long title.
  await page.setViewportSize({ width: 375, height: 812 });
  await setupApp(page, {
    library: [aSeries("s1", "Severance")],
    detail: { s1: seriesDetail("s1", "Severance", "95396") },
    episodes: {
      s1: [
        anEpisode("e1", 1, 1, "Good News About Hell, and Other Things That Happened", null, {
          stillUrl: "https://image.tmdb.org/t/p/original/still.jpg",
          overview: "Mark is promoted to lead the team after his friend leaves the company.",
        }),
      ],
    },
  });

  await page.goto("/series/s1");
  await page.getByRole("button", { name: /^Season 1 ·/ }).click();

  await expect(page.getByRole("button", { name: /Play in Infuse|Open in Infuse|Copy Infuse link/ })).toHaveCount(0);
  const pilot = page.getByRole("listitem").filter({ hasText: "S01E01" });
  const still = await pilot.locator("img").boundingBox();
  const expand = await pilot.getByRole("link", { name: "Manage", exact: true }).boundingBox();
  const title = await pilot.getByText("Good News About Hell, and Other Things That Happened").boundingBox();
  expect(still && expand && title).toBeTruthy();
  // The controls sit below the still rather than squeezing the text column to nothing…
  expect(expand!.y).toBeGreaterThanOrEqual(still!.y + still!.height);
  expect(title!.width).toBeGreaterThan(150);
  // …and nothing spills past the viewport.
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});

test("an episode row summarises what is on disk and expands onto the media surface a movie has", async ({ page }) => {
  let detailRequests = 0;
  page.on("request", (request) => {
    if (request.url().endsWith("/api/proxy/api/library/e1")) detailRequests++;
  });
  await setupApp(page, {
    role: "user",
    library: [aSeries("s1", "Severance")],
    detail: { s1: seriesDetail("s1", "Severance", "95396"), e1: pilotDetail() },
    episodes: {
      s1: [anEpisode("e1", 1, 1, "Good News About Hell", null, { media: pilotSummary }), anEpisode("e2", 1, 2, "Half Loop")],
    },
  });

  await page.goto("/series/s1");
  await page.getByRole("button", { name: /^Season 1 ·/ }).click();

  // The season reads at a glance; an episode with no file says so rather than showing nothing.
  await expect(page.getByText("HEVC 2160p · Dolby Vision 7 · HDR10 · 3.8 GB · 2 versions")).toBeVisible();
  await expect(page.getByText("No file")).toBeVisible();
  // Nothing is fetched for a row until it is opened — a long season must not carry every stream list.
  expect(detailRequests).toBe(0);

  await expect(page.getByRole("button", { name: "Show media of S01E01" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Manage", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Rename version" })).toHaveCount(0);
  expect(detailRequests).toBe(0);
});

test("an admin converts and extracts from an episode version, and sees its job above the seasons", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    library: [aSeries("s1", "Severance")],
    detail: { s1: seriesDetail("s1", "Severance", "95396"), e1: pilotDetail() },
    episodes: { s1: [anEpisode("e1", 1, 1, "Good News About Hell", null, { media: pilotSummary })] },
    transcodeAvailable: true,
    transcodeJobs: [aTranscodeJob("job-9", "e1", "Severance S01E01 - Remux.mkv")],
  });

  await page.goto("/manage/series/s1?episode=e1");

  // Every episode's jobs in one place, above the seasons: the card is named by its output file, which
  // carries the episode code, so no per-row block is needed.
  await expect(page.getByText("Conversions")).toBeVisible();
  await expect(page.getByText("Severance S01E01 - Remux.mkv")).toBeVisible();
  await expect(page.getByRole("button", { name: "Manage", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Cancel", exact: true })).toBeVisible();

  // Episode tracks use the same grid selection and confirmations as movie tracks.
  const version = page.locator("#source-source-1");
  await version.getByRole("button", { name: "Show tracks in Severance S01E01.mkv" }).click();
  await version.getByRole("checkbox", { name: /Select Video track 1/ }).check();
  await version.getByRole("checkbox", { name: /Select Audio track 2/ }).check();
  await page.getByRole("button", { name: "Convert", exact: true }).click();
  const convert = page.getByRole("dialog");
  await expect(convert.getByRole("heading", { name: "Convert selected tracks" })).toBeVisible();
  await expect(convert.getByText(/original files stay unchanged/)).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(convert).toHaveCount(0);

  await version.getByRole("checkbox", { name: /Select Video track 1/ }).uncheck();
  await expect(page.getByRole("button", { name: "Extract", exact: true })).toBeEnabled();
  await expect(page.getByRole("dialog")).toHaveCount(0);

  // Renaming previews the episode's own stem — the show and its episode code — not a title and year.
  await page.getByRole("button", { name: "Rename version" }).nth(1).click();
  const rename = page.getByRole("dialog");
  await expect(rename.getByText("Severance S01E01 - HEVC 1080p.mkv")).toBeVisible();
  await rename.getByLabel("Version").fill("Remux");
  await expect(rename.getByText("Severance S01E01 - Remux.mkv")).toBeVisible();
});
