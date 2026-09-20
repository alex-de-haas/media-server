import { expect, test } from "@playwright/test";
import { aMovie, aUserData, movieDetail, setupApp } from "./support";

const dates = ["2026-08-03T20:00:00Z", "2026-07-02T19:00:00Z", "2026-06-01T18:00:00Z", "2026-05-01T17:00:00Z"];

test("inline history expands, corrects an undated watch and deletes an individual rewatch", async ({ page }, testInfo) => {
  let entries = dates.map((watchedAt, index) => ({ id: `e${index}`, watchedAt }));
  let undated: { id: string; watchedAt: string | null }[] = [{ id: "unknown", watchedAt: null }];
  await setupApp(page, {
    detail: { m1: movieDetail("m1", "Arrival") },
    setWatchHistoryEntryTime: (id, watchedAt) => {
      if (id === "unknown") { undated = []; entries = [{ id, watchedAt }, ...entries]; }
    },
    deleteWatchHistoryEntry: (id) => { entries = entries.filter((entry) => entry.id !== id); },
  });
  await page.route("**/api/proxy/api/library/m1/watch-history?*", (route) => route.fulfill({
    json: { entries, undated, total: entries.length + undated.length, datedTotal: entries.length, offset: 0, limit: 20 },
  }));
  await page.goto("/movies/m1");
  const history = page.getByRole("region", { name: "Watch history" });
  await expect(history.getByRole("listitem")).toHaveCount(4);
  await expect(history.getByText("Date unknown", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("movie-history-desktop.png"), fullPage: true });
  await history.getByRole("button", { name: /Show all/ }).click();
  await expect(history.getByRole("listitem")).toHaveCount(5);
  await history.getByRole("button", { name: /Set time for/ }).click();
  await page.getByLabel("Watched at").fill("2026-08-04T19:45");
  await page.getByRole("button", { name: "Save time", exact: true }).click();
  await expect(history.getByText("Date unknown", { exact: true })).toHaveCount(0);
  await history.getByRole("button", { name: /Delete viewing/ }).first().click();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(history.getByRole("listitem")).toHaveCount(5);
  await history.getByRole("button", { name: /Delete viewing/ }).first().click();
  await page.getByRole("button", { name: "Delete", exact: true }).click();
  await expect(history.getByRole("listitem")).toHaveCount(4);
  await expect(history.getByRole("heading", { name: "Watch history · 4" })).toBeVisible();
});

test("failed history deletion retains the confirmation and the viewing", async ({ page }) => {
  await setupApp(page, { detail: { m1: movieDetail("m1", "Arrival") } });
  await page.route("**/api/proxy/api/library/m1/watch-history?*", (route) => route.fulfill({ json: {
    entries: [{ id: "e1", watchedAt: dates[0] }], undated: [], total: 1, datedTotal: 1, offset: 0, limit: 20,
  } }));
  await page.route("**/api/proxy/api/watch-history/entries/e1", (route) => route.fulfill({ status: 500, json: { error: "Try again" } }));
  await page.goto("/movies/m1");
  await page.getByRole("button", { name: /Delete viewing/ }).click();
  await page.getByRole("button", { name: "Delete", exact: true }).click();
  await expect(page.getByText("Couldn’t delete this play", { exact: true })).toBeVisible();
  await expect(page.getByRole("alertdialog")).toBeVisible();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("region", { name: "Watch history" }).getByRole("listitem")).toHaveCount(1);
});

test("removed movie permits rating changes and returns to the filtered grid after the last mark", async ({ page }) => {
  let gone = false;
  const detail = { ...movieDetail("g1", "Arrival"), removedAt: dates[0], catalogId: null, mediaSources: [], userData: aUserData({ userRating: 4 }) };
  await setupApp(page, { role: "user", catalogs: [{ id: "c1", name: "Movies", type: "Movie", root: "/movies", mountAvailable: true }], detail: { g1: detail } });
  await page.route("**/api/proxy/api/library/g1", (route) => gone
    ? route.fulfill({ status: 404, json: { error: "Not found" } })
    : route.fulfill({ json: detail }));
  await page.route("**/api/proxy/api/library/g1/rating", (route) => {
    if (route.request().method() === "DELETE") gone = true;
    else detail.userData.userRating = route.request().postDataJSON().rating;
    return route.fulfill({ json: detail.userData });
  });
  await page.goto("/movies/g1?catalog=c1&removed=1");
  await expect(page.getByRole("tab", { name: "Media", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /Play in Infuse|Mark watched/ })).toHaveCount(0);
  await page.getByRole("button", { name: "Rate 5 stars" }).click();
  await expect(page.getByRole("button", { name: "Rate 4 stars" })).toBeVisible();
  await page.getByRole("button", { name: "Clear your rating" }).click();
  await expect(page.getByRole("alertdialog")).toContainText("disappears from your removed list");
  await page.getByRole("button", { name: "Clear rating", exact: true }).click();
  await expect(page).toHaveURL(/\/movies\?catalog=c1&removed=1$/);
});

test("collection loads despite recommendation failure and its cards open movie details", async ({ page }) => {
  await setupApp(page, { detail: { m1: movieDetail("m1", "Arrival"), m2: movieDetail("m2", "Sibling") } });
  await page.route("**/api/proxy/api/library/m1/related/collection", (route) => route.fulfill({ json: { collectionName: "Saga", items: [aMovie("m2", "Sibling")] } }));
  await page.route("**/api/proxy/api/library/m1/related/similar", (route) => route.fulfill({ status: 503, body: "Unavailable" }));
  await page.goto("/movies/m1");
  await expect(page.getByRole("heading", { name: "More from Saga" })).toBeVisible();
  await expect(page.getByRole("region", { name: "Watch history" })).toBeVisible();
  await page.getByRole("tab", { name: "Tags", exact: true }).click();
  await expect(page.getByRole("heading", { name: "More from Saga" })).toBeVisible();
  await page.getByRole("link", { name: /Sibling/ }).click();
  await expect(page).toHaveURL(/\/movies\/m2$/);
});

for (const section of ["collection", "similar"] as const) {
  test(`${section} links preserve browse filters through details and back to the grid`, async ({ page }) => {
    await setupApp(page, {
      catalogs: [{ id: "c1", name: "Movies", type: "Movie", root: "/movies", mountAvailable: true }],
      detail: { m1: movieDetail("m1", "Arrival"), m2: movieDetail("m2", "Contact") },
    });
    await page.route(`**/api/proxy/api/library/m1/related/${section}`, (route) => route.fulfill({
      json: { collectionName: "Saga", items: [aMovie("m2", "Contact")] },
    }));
    await page.goto("/movies/m1?catalog=c1&removed=1");
    await page.getByRole("link", { name: /Contact/ }).click();
    await expect(page).toHaveURL(/\/movies\/m2\?catalog=c1&removed=1$/);
    await expect(page.getByRole("heading", { name: "Contact", exact: true })).toBeVisible();
    await page.locator('a[href="/movies?catalog=c1&removed=1"]').click();
    await expect(page).toHaveURL(/\/movies\?catalog=c1&removed=1$/);
  });
}

test("movie history stays usable on a narrow viewport", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await setupApp(page, { detail: { m1: movieDetail("m1", "Arrival") } });
  await page.goto("/movies/m1");
  const history = page.getByRole("region", { name: "Watch history" });
  await expect(history.getByRole("button", { name: "Log watch", exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("movie-history-mobile.png"), fullPage: true });
  await history.getByRole("button", { name: "Log watch", exact: true }).click();
  await expect(page.getByLabel("Watched at")).toBeVisible();
  await expect(page.getByRole("button", { name: "Log watch", exact: true }).last()).toBeVisible();
});

test("long history pages without losing unknown dates and logs a watch on a removed movie", async ({ page }) => {
  let logged = false;
  await setupApp(page, {
    detail: { g1: { ...movieDetail("g1", "Arrival"), removedAt: dates[0], catalogId: null } },
    logWatch: () => { logged = true; },
  });
  await page.route("**/api/proxy/api/library/g1/watch-history?*", (route) => {
    const offset = Number(new URL(route.request().url()).searchParams.get("offset"));
    const entries = Array.from({ length: 23 }, (_, index) => ({ id: `e${index}`, watchedAt: dates[0] }));
    return route.fulfill({ json: {
      entries: entries.slice(offset, offset + 20), undated: [{ id: "unknown", watchedAt: null }],
      total: 24, datedTotal: 23, offset, limit: 20,
    } });
  });
  await page.goto("/movies/g1");
  const history = page.getByRole("region", { name: "Watch history" });
  await history.getByRole("button", { name: /Show all/ }).click();
  await expect(history.getByRole("listitem")).toHaveCount(21);
  await history.getByRole("button", { name: "Load more", exact: true }).click();
  await expect(history.getByRole("listitem")).toHaveCount(24);
  await expect(history.getByText("Date unknown", { exact: true })).toHaveCount(1);
  await history.getByRole("button", { name: "Log watch", exact: true }).click();
  await page.getByLabel("Watched at").fill("2026-08-03T18:00");
  await page.getByRole("dialog").getByRole("button", { name: "Log watch", exact: true }).click();
  await expect.poll(() => logged).toBe(true);
});

test("related recommendations stay outside the tabs and navigate to available titles", async ({ page }) => {
  await setupApp(page, { detail: { m1: movieDetail("m1", "Arrival"), m2: movieDetail("m2", "Contact") } });
  await page.route("**/api/proxy/api/library/m1/related/similar", (route) => route.fulfill({ json: {
    collectionName: null, items: [aMovie("m2", "Contact")],
  } }));
  await page.goto("/movies/m1");
  await page.getByRole("tab", { name: "Media", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Similar movies in your library" })).toBeVisible();
  await page.getByRole("link", { name: /Contact/ }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Contact", exact: true })).toBeVisible();
});

test("admin removed actions offer permanent deletion without file operations", async ({ page }) => {
  await setupApp(page, { role: "admin", detail: { g1: { ...movieDetail("g1", "Arrival"), removedAt: dates[0], catalogId: null } } });
  await page.goto("/movies/g1?removed=1");
  await page.getByRole("button", { name: "More actions" }).click();
  await expect(page.getByRole("menuitem", { name: /Refresh|Move to|Fix match|Choose poster/ })).toHaveCount(0);
  await page.getByRole("menuitem", { name: /Delete permanently/ }).click();
  await expect(page.getByRole("alertdialog")).toContainText("every user’s history");
  const deleted = page.waitForRequest((request) => request.url().endsWith("/library/removed/g1") && request.method() === "DELETE");
  await page.getByRole("button", { name: "Delete permanently", exact: true }).click();
  await deleted;
  await expect(page).toHaveURL(/\/movies\?removed=1$/);
});
