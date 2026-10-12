import { expect, test } from "@playwright/test";
import { aMovie, aRemovedTitle, anEpisode, aSeason, aTranscodeJob, movieDetail, seriesDetail, setupApp, managementPage, managementRow } from "./support";

const movie = { ...movieDetail("m1", "Arrival"), catalogRoot: "/media/", contentPath: "Arrival (2016)", mediaSources: [
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
  await expect(page.getByText("/media/Arrival (2016)", { exact: true })).toBeVisible();
  await expect(page.locator("dl").getByText("Catalog", { exact: true })).toHaveCount(0);
  await expect(page.locator("dl").getByText("Folder", { exact: true })).toHaveCount(0);
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
  await expect(page).toHaveURL("/manage/movies?expanded=m1");
  await expect(page.locator("#source-dv7")).toBeVisible();
});

test("legacy movie links open the containing list page and preserve source targeting", async ({ page }) => {
  const queries: URLSearchParams[] = [];
  const target = { ...movie, id: "m42", title: "Film 042" };
  await setupApp(page, { detail: { m42: target }, management: query => {
    queries.push(query);
    const offset = query.get("focusId") === "m42" ? 25 : Number(query.get("offset") ?? 0);
    return managementPage(offset === 25 ? [managementRow("m25", "Film 025"), managementRow("m42", target.title)] : [managementRow("m0", "Film 000")], { total: 60, offset });
  } });
  await page.goto("/manage/movies/m42?source=compatible");
  await expect(page).toHaveURL(url => url.pathname === "/manage/movies" && url.searchParams.get("offset") === "25" && url.searchParams.get("source") === "compatible" && !url.searchParams.has("focus"));
  await expect(page.getByText("Film 025", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Hide versions of Film 042" })).toBeVisible();
  await expect(page.locator("#source-compatible")).toBeVisible();
  expect(queries.every(query => !query.has("itemId"))).toBe(true);
  await page.getByRole("button", { name: "Go to previous page", exact: true }).click();
  await expect(page.getByText("Film 000", { exact: true })).toBeVisible();
  await page.goBack();
  await expect(page.getByRole("button", { name: "Hide versions of Film 042" })).toBeVisible();
});

for (const state of ["Running", "Completed"]) {
  test(`${state} conversions link to Management from Activity but omit the link inside Management`, async ({ page }) => {
    await setupApp(page, { detail: { m1: movie }, transcodeJobs: [{ ...aTranscodeJob("j1", "m1", "Arrival - HEVC.mkv", state), mediaSourceId: "compatible" }] });
    await page.goto("/manage/movies?expanded=m1");
    await expect(page.getByRole("table", { name: "Versions of Arrival", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Manage", exact: true })).toHaveCount(0);
    if (state === "Running") {
      await expect(page.getByText("Arrival - HEVC.mkv", { exact: true })).toBeVisible();
      await expect(page.getByRole("button", { name: "Cancel", exact: true })).toBeVisible();
    } else {
      await expect(page.getByText("Conversions", { exact: true })).toHaveCount(0);
      await expect(page.getByRole("button", { name: "Dismiss", exact: true })).toHaveCount(0);
    }
    await page.goto("/activity");
    if (state === "Completed") await page.getByRole("tab", { name: /^Done/ }).click();
    if (state === "Running") {
      await page.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(page.getByRole("alertdialog")).toContainText("Arrival - HEVC.mkv");
      await page.getByRole("button", { name: "Keep running", exact: true }).click();
    }
    await page.getByRole("button", { name: "Manage", exact: true }).click();
    await expect(page).toHaveURL(url => url.pathname === "/manage/movies" && url.searchParams.get("expanded") === "m1" && url.searchParams.get("source") === "compatible" && !url.searchParams.has("focus"));
    await expect(page.getByRole("button", { name: "Hide versions of Arrival" })).toBeVisible();
    await expect(page.locator("#source-compatible")).toBeInViewport();
  });
}

test("cancelling a conversion requires confirmation and hides it only after successful cancellation", async ({ page }) => {
  const job = aTranscodeJob("j1", "m1", "Arrival - HEVC.mkv");
  await setupApp(page, { detail: { m1: movie }, transcodeJobs: [job] });
  let attempts = 0;
  const removed: string[] = [];
  page.on("request", request => { if (request.method() === "DELETE") removed.push(request.url()); });
  await page.route("**/api/proxy/api/transcode/j1/cancel", route => {
    attempts++;
    if (attempts === 1) return route.fulfill({ status: 503, json: { detail: "Encoder is unavailable" } });
    job.state = "Cancelled";
    return route.fulfill({ status: 204, body: "" });
  });
  await page.goto("/manage/movies?expanded=m1");
  const cancel = page.getByRole("button", { name: "Cancel", exact: true });
  await cancel.click();
  const confirmation = page.getByRole("alertdialog");
  await expect(confirmation).toContainText(job.name);
  expect(attempts).toBe(0);
  await confirmation.getByRole("button", { name: "Keep running", exact: true }).click();
  await expect(confirmation).toHaveCount(0);
  await cancel.click();
  await page.keyboard.press("Escape");
  await expect(confirmation).toHaveCount(0);
  expect(attempts).toBe(0);
  await cancel.click();
  await confirmation.getByRole("button", { name: "Cancel conversion", exact: true }).click();
  await expect(page.getByText("Couldn’t cancel", { exact: true })).toBeVisible();
  await expect(confirmation).toBeVisible();
  await expect(page.getByText("Conversions", { exact: true })).toBeVisible();
  await confirmation.getByRole("button", { name: "Cancel conversion", exact: true }).click();
  await expect(confirmation).toHaveCount(0);
  await expect(page.getByText("Conversions", { exact: true })).toHaveCount(0);
  await expect(page.getByText(job.name, { exact: true })).toHaveCount(0);
  expect(attempts).toBe(2);
  expect(removed).toEqual([]);
  await page.reload();
  await expect(page.getByRole("table", { name: "Versions of Arrival", exact: true })).toBeVisible();
  await expect(page.getByText("Conversions", { exact: true })).toHaveCount(0);
  await page.goto("/activity");
  await page.getByRole("tab", { name: /^Done/ }).click();
  await expect(page.getByText(job.name, { exact: true })).toBeVisible();
});

test("completed conversions disappear as polling observes them and refresh the produced versions", async ({ page }) => {
  const jobs = [aTranscodeJob("j1", "m1", "First conversion"), aTranscodeJob("j2", "m1", "Second conversion", "Queued")];
  const detail = { ...movie, mediaSources: [...movie.mediaSources] };
  await setupApp(page, { detail: { m1: detail }, transcodeJobs: jobs });
  const removed: string[] = [];
  page.on("request", request => { if (request.method() === "DELETE") removed.push(request.url()); });
  await page.goto("/manage/movies?expanded=m1");
  await expect(page.getByText("First conversion", { exact: true })).toBeVisible();
  jobs[0].state = "Completed";
  await expect(page.getByText("First conversion", { exact: true })).toHaveCount(0, { timeout: 8_000 });
  await expect(page.getByText("Second conversion", { exact: true })).toBeVisible();
  detail.mediaSources.push({ ...movie.mediaSources[0], id: "converted", fileName: "Arrival - HEVC.mkv", name: "Converted" });
  jobs[1].state = "Completed";
  await expect(page.getByText("Conversions", { exact: true })).toHaveCount(0, { timeout: 8_000 });
  await expect(page.locator("#source-converted")).toBeVisible();
  expect(removed).toEqual([]);
});

test("failed conversions retain the error in Management", async ({ page }) => {
  await setupApp(page, { detail: { m1: movie }, transcodeJobs: [{ ...aTranscodeJob("j1", "m1", "Arrival - HEVC.mkv", "Failed"), error: "Encoder is unavailable" }] });
  await page.goto("/manage/movies?expanded=m1");
  await expect(page.getByText("Conversions", { exact: true })).toBeVisible();
  await expect(page.getByText("Encoder is unavailable", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Dismiss", exact: true })).toBeVisible();
});

test("a missing movie keeps the list usable and links to its retained history", async ({ page }) => {
  await setupApp(page, { management: () => managementPage([managementRow("m1", "Arrival")]), removedTitles: [aRemovedTitle("g1", "The Black Phone")] });
  await page.goto("/manage/movies/g1");
  await expect(page.getByText("This movie is no longer in the published list or does not match these filters.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Show versions of Arrival" })).toBeVisible();
  await page.getByRole("link", { name: "View retained title" }).click();
  await expect(page.getByRole("dialog")).toContainText("The Black Phone");
  await expect(page.getByRole("button", { name: "Delete permanently", exact: true })).toBeVisible();
});

test("removed movies retain administrative access through the list route", async ({ page }) => {
  await setupApp(page, {
    detail: { g1: { ...movieDetail("g1", "The Black Phone"), removedAt: "2026-08-01T00:00:00Z", catalogId: null } },
    removedTitles: [aRemovedTitle("g1", "The Black Phone")],
  });
  await page.goto("/movies/g1?removed=1");
  await page.getByRole("button", { name: "More actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Manage", exact: true }).click();
  await expect(page).toHaveURL("/manage/movies?removed=1&focus=g1&expanded=g1");
  await expect(page.getByRole("dialog")).toContainText("The Black Phone");
  await page.getByRole("button", { name: "Delete permanently", exact: true }).click();
  await expect(page.getByRole("alertdialog")).toContainText("for every user");
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await page.getByRole("button", { name: "Close", exact: true }).click();
  await expect(page).toHaveURL("/manage/movies");
});

for (const [link, sort, view] of [["1", "issues", "attention"], ["Severance", "title", "all"]] as const) {
  test(`series ${sort} drill-down resets ordering, preserves filters and loads versions lazily`, async ({ page }) => {
    const episode = managementRow("e1", "Pilot", { kind: "Episode", parentIndexNumber: 1, indexNumber: 1, indexNumberEnd: 2, issueCount: 1 });
    const episodeRequests: URLSearchParams[] = [];
    await setupApp(page, { detail: { s1: { ...seriesDetail("s1", "Severance"), seasons: [aSeason("season1", 1, 1)] } }, episodes: { s1: [anEpisode("e1", 1, 1, "Pilot")] },
      management: q => {
        if (q.get("kind") === "Episode") episodeRequests.push(q);
        return managementPage(q.get("kind") === "Episode" ? [episode] : [managementRow("s1", "Severance", { kind: "Series", affectedEpisodes: 1, episodeCount: 2 })]);
      } });
    await page.goto(`/manage/series?view=all&resolution=2160p&sort=${sort}&descending=true`);
    await page.getByRole("link", { name: link, exact: true }).click();
    await expect(page).toHaveURL(`/manage/series/s1?view=${view}&resolution=2160p`);
    await expect(page.getByText("S01E01-E02", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Show versions of Pilot" })).toBeVisible();
    expect(episodeRequests.some(q => q.get("sort") === "episode" && q.get("descending") === "false")).toBe(true);
  });
}

test("management remains usable on a narrow viewport and when embedded", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await setupApp(page, { detail: { m1: movie }, management: () => managementPage(rows) });
  await page.goto("/manage/movies?hosty_launch=embedded");
  await expect(page.getByRole("heading", { name: "Library management" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("data-hosty-launch", "embedded");
  await expect(page.getByRole("button", { name: "Show versions of Arrival" })).toBeVisible();
  await page.getByRole("button", { name: "Show versions of Arrival" }).click();
  await expect(page.getByRole("table", { name: "Versions of Arrival", exact: true })).toBeVisible();
  await expect(page.locator("#source-compatible")).toBeVisible();
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
    await page.getByRole("row").filter({ hasText: "S01E01" }).getByRole("button", { name: "More actions", exact: true }).click();
    await page.getByRole("menuitem", { name: /Fix match/ }).click();
    await page.getByRole("textbox", { name: "Series title" }).fill("Dark");
    await page.getByRole("dialog").getByRole("button", { name: /Search/ }).click();
    await page.getByRole("button", { name: /Dark \(2017\)/ }).click();
    await expect(page).toHaveURL(`/manage/series/s2?episode=${targetId}`);
    await expect(page.getByRole("button", { name: "Hide versions of Corrected pilot" })).toBeVisible();
  });
}
