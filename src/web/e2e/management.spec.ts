import { expect, test } from "@playwright/test";
import { aMovie, aRemovedTitle, anEpisode, aSeason, movieDetail, seriesDetail, setupApp, managementPage, managementRow } from "./support";

const movie = { ...movieDetail("m1", "Arrival"), mediaSources: [
  { versionName: "Copy", fileName: "copy.mkv", container: "mkv", sourceKind: "File", sizeBytes: 1024, durationTicks: 60_000_000, bitrate: null, streams: [], id: "dv7", name: "Original", path: "/media/Arrival/original.mkv" },
  { versionName: "Copy", fileName: "copy.mkv", container: "mkv", sourceKind: "File", sizeBytes: 1024, durationTicks: 60_000_000, bitrate: null, streams: [], id: "compatible", name: "Compatible", path: "/media/Arrival/compatible.mkv" },
] };
const rows = [managementRow("m1", "Arrival", { versionCount: 2, issueCount: 1 }), managementRow("m2", "Dune")];
const diagnostics = { m1: [
  { id: "dv7", availability: "available" as const, reason: null, dolbyVisionFallback: true, dolbyVisionUnknown: false, matchesFilter: true },
  { id: "compatible", availability: "available" as const, reason: null, dolbyVisionFallback: false, dolbyVisionUnknown: false, matchesFilter: false },
] };

test("movies expand lazily, keep all versions under a filter and restore expansion on reload", async ({ page }) => {
  await setupApp(page, { detail: { m1: movie }, diagnostics, management: q => managementPage(q.get("view") === "dolby-vision" ? rows.slice(0, 1) : rows, { counts: { all: 2, attention: 1, unavailable: 0, bluray: 0, "dolby-vision": 1, active: 0 } }) });
  const details: string[] = [];
  page.on("request", request => { if (/\/library\/m[12]$/.test(request.url())) details.push(request.url()); });
  await page.goto("/manage/movies");
  await expect(page.getByRole("button", { name: "Show versions of Arrival" })).toBeVisible();
  expect(details).toEqual([]);
  await page.getByRole("button", { name: /Dolby Vision limitations/ }).click();
  const expand = page.getByRole("button", { name: "Show versions of Arrival" });
  await expand.focus(); await page.keyboard.press("Enter");
  await expect(page.getByText("Matches filters", { exact: true })).toHaveCount(1);
  await expect(page.locator("#source-dv7")).toBeVisible();
  await expect(page.locator("#source-compatible")).toBeVisible();
  await expect(page).toHaveURL(/view=dolby-vision.*expanded=m1/);
  await page.reload();
  await expect(page.getByRole("button", { name: "Hide versions of Arrival" })).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator("#source-compatible")).toBeVisible();
  await page.getByRole("button", { name: "Hide versions of Arrival" }).click();
  await expect(page.locator("#source-dv7")).toHaveCount(0);
  await page.goBack();
  await expect(page.locator("#source-dv7")).toBeVisible();
});

test("viewers cannot open management or fetch its diagnostics", async ({ page }) => {
  await setupApp(page, { role: "user" });
  const requests: string[] = [];
  page.on("request", request => { if (request.url().includes("/api/management/")) requests.push(request.url()); });
  await page.goto("/manage/movies/m1");
  await expect(page.getByText("Administrator access required")).toBeVisible();
  expect(requests).toEqual([]);
});

test("browse preserves personal actions and exposes only a Manage link", async ({ page }) => {
  await setupApp(page, { library: [aMovie("m1", "Arrival")], detail: { m1: movie }, availability: [{ id: "m1", availability: "available", dolbyVisionFallback: false }] });
  await page.goto("/movies/m1");
  await expect(page.getByRole("button", { name: "Rate 4 stars" })).toBeVisible();
  await expect(page.getByText("/media/Arrival/original.mkv")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Delete this version" })).toHaveCount(0);
  await page.getByRole("button", { name: "More actions" }).click();
  await expect(page.getByRole("menuitem", { name: "Log watch…" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Refresh media data" })).toHaveCount(0);
  await page.getByRole("menuitem", { name: "Manage", exact: true }).click();
  await expect(page).toHaveURL("/manage/movies/m1");
  await expect(page.locator("#source-dv7")).toBeVisible();
});

test("series issue count opens the episode subset and source details are lazy", async ({ page }) => {
  const episode = managementRow("e1", "Pilot", { kind: "Episode", parentIndexNumber: 1, indexNumber: 1, indexNumberEnd: 2, issueCount: 1 });
  await setupApp(page, { detail: { s1: { ...seriesDetail("s1", "Severance"), seasons: [aSeason("season1", 1, 1)] } }, episodes: { s1: [anEpisode("e1", 1, 1, "Pilot")] },
    management: q => managementPage(q.get("kind") === "Episode" ? [episode] : [managementRow("s1", "Severance", { kind: "Series", affectedEpisodes: 1, episodeCount: 2 })]) });
  await page.goto("/manage/series?view=all");
  await page.getByRole("link", { name: "1", exact: true }).click();
  await expect(page).toHaveURL(/\/manage\/series\/s1\?.*view=attention/);
  await expect(page.getByText("S01E01-E02", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Show versions of Pilot" })).toBeVisible();
});

test("management remains usable on a narrow viewport and when embedded", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await setupApp(page, { detail: { m1: movie }, management: () => managementPage(rows) });
  await page.goto("/manage/movies?hosty_launch=embedded");
  await expect(page.getByRole("heading", { name: "Library management" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("data-hosty-launch", "embedded");
  await expect(page.getByRole("button", { name: "Show versions of Arrival" })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("management-mobile.png"), fullPage: true });
});

test("list failures can be retried and empty filters can be reset", async ({ page }) => {
  await setupApp(page, {});
  let fail = true;
  await page.route("**/api/proxy/api/management/items?*", route => fail ? route.fulfill({ status: 503, json: { detail: "Temporary failure" } }) : route.fulfill({ json: managementPage([]) }));
  await page.goto("/manage/movies?sourceKind=bluray");
  await expect(page.getByText("Temporary failure")).toBeVisible();
  fail = false; await page.getByRole("button", { name: "Retry", exact: true }).click();
  await expect(page.getByText("No titles match these filters.")).toBeVisible();
  await page.getByRole("button", { name: "Reset", exact: true }).click();
  await expect(page).toHaveURL("/manage/movies");
});

test("technical filters, server ordering and paging preserve URL context", async ({ page }) => {
  const requests: string[] = [];
  await setupApp(page, { management: query => {
    requests.push(query.toString());
    return managementPage(rows, { total: 80, offset: Number(query.get("offset") ?? 0), limit: Number(query.get("limit") ?? 25) });
  } });
  await page.goto("/manage/movies");
  await page.getByRole("button", { name: "Add filter", exact: true }).click();
  await page.getByRole("option", { name: "Resolution", exact: true }).click();
  await page.getByRole("option", { name: "is any of", exact: true }).click();
  await page.getByRole("option", { name: "2160p", exact: true }).click();
  await page.getByRole("option", { name: "1080p", exact: true }).click();
  await page.keyboard.press("Escape");
  await expect(page).toHaveURL(/resolution=2160p/);
  await page.getByRole("button", { name: "Title", exact: true }).click();
  await expect(page).toHaveURL(/sort=title/);
  await page.getByRole("button", { name: "Go to next page", exact: true }).click();
  await expect(page).toHaveURL(/offset=25/);
  expect(requests.some(query => query.includes("resolution=2160p") && query.includes("offset=25"))).toBe(true);
  await page.reload();
  await expect(page.getByRole("button", { name: "2 selected", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "2 selected", exact: true }).click();
  await expect(page.getByRole("option", { name: "2160p", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("option", { name: "1080p", exact: true })).toHaveAttribute("aria-selected", "true");
});

test("submitted search follows browser history", async ({ page }) => {
  await setupApp(page, { management: () => managementPage(rows) });
  await page.goto("/manage/movies");
  await page.getByRole("textbox", { name: "Search titles" }).fill("Arrival");
  await page.getByRole("button", { name: "Search", exact: true }).click();
  await expect(page).toHaveURL(/search=Arrival/);
  await page.goBack();
  await expect(page.getByRole("textbox", { name: "Search titles" })).toHaveValue("");
  await page.goForward();
  await expect(page.getByRole("textbox", { name: "Search titles" })).toHaveValue("Arrival");
});

test("retained series administration moves out of the personal removed dialog", async ({ page }) => {
  await setupApp(page, { removedTitles: [aRemovedTitle("s1", "Dark", { kind: "Series", isFavorite: true })] });
  await page.goto("/series?removed=1");
  await page.getByRole("button", { name: /Dark/ }).click();
  const personal = page.getByRole("dialog");
  await expect(personal.getByRole("button", { name: "Unfavorite", exact: true })).toBeVisible();
  await expect(personal.getByRole("button", { name: "Delete permanently", exact: true })).toHaveCount(0);
  await personal.getByRole("link", { name: "Manage", exact: true }).click();
  await expect(page).toHaveURL("/manage/series/s1?removed=1");
  await expect(page.getByRole("dialog").getByRole("button", { name: "Unfavorite", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Delete permanently", exact: true }).click();
  await expect(page.getByRole("alertdialog")).toContainText("for every user");
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("dialog")).toBeVisible();
});

test("poster warnings describe title availability, not the presence of an affected copy", async ({ page }) => {
  await setupApp(page, { library: [aMovie("m1", "Arrival"), aMovie("m2", "Dune"), aMovie("m3", "Blade Runner")], availability: [
    { id: "m1", availability: "available", dolbyVisionFallback: false },
    { id: "m2", availability: "blocked", dolbyVisionFallback: false },
    { id: "m3", availability: "pending", dolbyVisionFallback: false },
  ] });
  await page.goto("/movies");
  await expect(page.getByRole("link", { name: /Arrival/ })).not.toContainText("Unavailable");
  await expect(page.getByRole("link", { name: /Dune/ })).toContainText("Unavailable");
  await expect(page.getByRole("link", { name: /Blade Runner/ })).toContainText("Preparing");
});

for (const targetId of ["e1", "e2"]) {
  test(`episode remapping follows its current series when the target is ${targetId}`, async ({ page }) => {
    await setupApp(page, {
      detail: { s1: seriesDetail("s1", "Wrong show"), s2: seriesDetail("s2", "Dark") },
      episodes: { s1: [anEpisode("e1", 1, 1, "Pilot")], s2: [anEpisode(targetId, 1, 1, "Corrected pilot")] },
      metadataSearch: [{ reference: { provider: "tmdb", id: "70523" }, title: "Dark", year: 2017, score: 1 }],
      remapTargetId: targetId,
    });
    await page.route(`**/api/proxy/api/management/items/${targetId}/location`, route => route.fulfill({ json: { id: targetId, kind: "Episode", seriesId: "s2" } }));
    await page.goto("/manage/series/s1");
    await page.getByRole("row").filter({ hasText: "S01E01" }).getByRole("button", { name: "Actions", exact: true }).click();
    await page.getByRole("menuitem", { name: /Fix match/ }).click();
    await page.getByRole("textbox", { name: "Series title" }).fill("Dark");
    await page.getByRole("dialog").getByRole("button", { name: /Search/ }).click();
    await page.getByRole("button", { name: /Dark \(2017\)/ }).click();
    await expect(page).toHaveURL(`/manage/series/s2?episode=${targetId}`);
    await expect(page.getByRole("button", { name: "Hide versions of Corrected pilot" })).toBeVisible();
  });
}
