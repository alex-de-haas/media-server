import { test, expect } from "@playwright/test";
import { anEpisode, aMovie, aSeries, movieDetail, seriesDetail, setupApp } from "./support";

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

  // Keeping the files is the default: a delete must not silently remove media from disk.
  const kept = page.waitForRequest(
    (request) =>
      request.url().includes("/api/proxy/api/library/episodes/e1?deleteFiles=false") && request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Delete S01E01" }).click();
  await expect(page.getByRole("heading", { name: "Delete episode?" })).toBeVisible();
  await page.getByRole("button", { name: "Remove from library" }).click();
  await kept;
  await expect(page).toHaveURL(/\/series\/s1$/);

  // Ticking the box is what escalates to erasing the file.
  const erased = page.waitForRequest(
    (request) =>
      request.url().includes("/api/proxy/api/library/episodes/e2?deleteFiles=true") && request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Delete S01E02" }).click();
  await page.getByRole("checkbox", { name: /Delete files from disk/ }).click();
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
      request.url().includes("/api/proxy/api/library/seasons/season-1?deleteFiles=false") &&
      request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Delete season 1" }).click();
  await expect(page.getByText("1 episode will be removed from the library.")).toBeVisible();
  await page.getByRole("button", { name: "Remove from library" }).click();
  await deleted;

  // The page it was called from no longer exists.
  await expect(page).toHaveURL(/\/series$/);
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
