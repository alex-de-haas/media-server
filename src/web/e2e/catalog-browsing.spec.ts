import { expect, test } from "@playwright/test";
import { aCatalog, aMovie, aSeries, movieDetail, setupApp } from "./support";

const MOVIES_HD = "11111111-1111-1111-1111-111111111111";
const MOVIES_4K = "22222222-2222-2222-2222-222222222222";
const SERIES = "33333333-3333-3333-3333-333333333333";
const ANIME = "44444444-4444-4444-4444-444444444444";

test("filters movies by catalog and preserves the filter through detail navigation", async ({ page }) => {
  await setupApp(page, {
    catalogs: [
      aCatalog(MOVIES_HD, "Movies HD", "Movie"),
      aCatalog(MOVIES_4K, "Movies 4K", "Movie"),
      aCatalog(SERIES, "Series", "Series"),
    ],
    library: [
      { ...aMovie("m1", "Arrival"), catalogId: MOVIES_HD },
      { ...aMovie("m2", "Dune"), catalogId: MOVIES_4K },
    ],
    detail: { m2: { ...movieDetail("m2", "Dune"), catalogId: MOVIES_4K } },
  });

  await page.goto("/movies?search=desert&page=2");
  await expect(page.getByRole("link", { name: /Arrival/ })).toBeVisible();
  await expect(page.getByRole("link", { name: /Dune/ })).toBeVisible();

  const filteredRequest = page.waitForRequest((request) => {
    const url = new URL(request.url());
    return url.pathname.endsWith("/api/proxy/api/library")
      && url.searchParams.get("kind") === "Movie"
      && url.searchParams.get("catalogId") === MOVIES_4K;
  });
  await page.getByRole("combobox", { name: "Filter movies by catalog" }).click();
  await page.getByRole("option", { name: "Movies 4K" }).click();
  await filteredRequest;

  await expect(page).toHaveURL(`/movies?search=desert&page=2&catalog=${MOVIES_4K}`);
  await expect(page.getByRole("combobox", { name: "Filter movies by catalog" })).toContainText("Movies 4K");
  await expect(page.getByRole("link", { name: /Dune/ })).toBeVisible();
  await expect(page.getByRole("link", { name: /Arrival/ })).toHaveCount(0);

  await page.getByRole("link", { name: /Dune/ }).click();
  await expect(page).toHaveURL(`/movies/m2?catalog=${MOVIES_4K}`);
  await page.getByRole("main").getByRole("link", { name: "Movies" }).click();
  await expect(page).toHaveURL(`/movies?catalog=${MOVIES_4K}`);

  await page.reload();
  await expect(page.getByRole("link", { name: /Dune/ })).toBeVisible();
  await expect(page.getByRole("link", { name: /Arrival/ })).toHaveCount(0);
});

test("offers only applicable catalogs and keeps offline catalogs visible", async ({ page }) => {
  await setupApp(page, {
    catalogs: [
      aCatalog(MOVIES_HD, "Movies HD", "Movie"),
      aCatalog(SERIES, "Drama", "Series"),
      aCatalog(ANIME, "Anime Archive", "Anime", false),
    ],
    library: [
      { ...aSeries("s1", "Severance"), catalogId: SERIES },
      { ...aSeries("s2", "Monster"), catalogId: ANIME },
    ],
  });

  await page.goto("/series");
  await page.getByRole("combobox", { name: "Filter series by catalog" }).click();

  await expect(page.getByRole("option", { name: "Drama" })).toBeVisible();
  await expect(page.getByRole("option", { name: "Anime Archive (Offline)" })).toBeVisible();
  await expect(page.getByRole("option", { name: "Movies HD" })).toHaveCount(0);
});

test("hides the catalog filter when there is only one applicable catalog", async ({ page }) => {
  await setupApp(page, {
    catalogs: [
      aCatalog(MOVIES_HD, "Movies HD", "Movie"),
      aCatalog(SERIES, "Series", "Series"),
    ],
    library: [{ ...aMovie("m1", "Arrival"), catalogId: MOVIES_HD }],
  });

  await page.goto("/movies");
  await expect(page.getByRole("link", { name: /Arrival/ })).toBeVisible();
  await expect(page.getByRole("combobox", { name: "Filter movies by catalog" })).toHaveCount(0);
});

test("removes only an invalid catalog from the current URL", async ({ page }) => {
  await setupApp(page, {
    catalogs: [
      aCatalog(MOVIES_HD, "Movies HD", "Movie"),
      aCatalog(MOVIES_4K, "Movies 4K", "Movie"),
    ],
    library: [{ ...aMovie("m1", "Arrival"), catalogId: MOVIES_HD }],
  });

  await page.goto("/movies?search=arrival&catalog=missing&page=2");

  await expect(page).toHaveURL("/movies?search=arrival&page=2");
  await expect(page.getByRole("link", { name: /Arrival/ })).toBeVisible();
});

test("admin opens a catalog directly in its matching media page", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    catalogs: [aCatalog(ANIME, "Anime Archive", "Anime")],
  });

  await page.goto("/settings?tab=catalogs");
  await page.getByRole("button", { name: "Catalog actions" }).click();
  await page.getByRole("menuitem", { name: "Browse media" }).click();

  await expect(page).toHaveURL(`/series?catalog=${ANIME}`);
});


test("library posters use compact accessible captions and preserve artwork fallbacks", async ({ page }) => {
  await setupApp(page, { library: [
    { ...aMovie("m1", "Arrival"), posterUrl: "/poster.svg", videoFormats: ["HDR10", "Dolby Vision"] },
    { ...aMovie("m2", "Missing artwork"), year: null, videoFormats: [] },
    // A series' badges are the union of its episodes'; one whose episodes were never probed has none.
    { ...aSeries("s1", "Severance"), videoFormats: ["HDR10"] },
    { ...aSeries("s2", "Unprobed"), videoFormats: [] },
  ] });
  await page.route("**/poster.svg", route => route.fulfill({ contentType: "image/svg+xml",
    body: '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="300"><rect width="200" height="300" fill="#345"/></svg>' }));
  await page.goto("/movies");
  const card = page.getByRole("link", { name: /Arrival/ });
  await expect(card).toBeVisible();
  await expect(card).toContainText("2016 · HDR10 · Dolby Vision");
  await expect(card.getByText("Arrival", { exact: true })).toHaveClass("sr-only");
  await expect(page.getByRole("link", { name: /Missing artwork/ }).locator('[aria-hidden]')).toContainText("Missing artwork");
  await page.goto("/series");
  await expect(page.getByRole("link", { name: /Severance/ })).toContainText("2022 · HDR10");
  const unprobed = page.getByRole("link", { name: /Unprobed/ });
  await expect(unprobed).toContainText("2022");
  await expect(unprobed).not.toContainText(" · ");
});

test("storage alerts follow the selected catalog and link admins to settings", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    catalogs: [aCatalog(MOVIES_HD, "Movies HD", "Movie", false), aCatalog(MOVIES_4K, "Movies 4K", "Movie"),
      aCatalog(SERIES, "Drama", "Series", false)],
    library: [{ ...aMovie("m1", "Arrival"), catalogId: MOVIES_HD }],
  });
  await page.goto("/movies");
  const alert = page.getByRole("alert").filter({ hasText: "Storage unavailable" });
  await expect(alert).toContainText("Movies HD: storage is offline.");
  await expect(alert).not.toContainText("Drama");
  await expect(page.getByRole("link", { name: /Arrival/ })).toBeVisible();
  await alert.getByRole("link", { name: "Manage catalogs" }).click();
  await expect(page).toHaveURL("/settings?tab=catalogs");
  await expect(page.getByRole("button", { name: "Scan all", exact: true })).toBeVisible();
  await page.goto(`/movies?catalog=${MOVIES_4K}`);
  await expect(page.getByRole("combobox", { name: "Filter movies by catalog" })).toContainText("Movies 4K");
  await expect(alert).toHaveCount(0);
});

test("a single unanchored anime catalog alerts regular users and clears on recovery", async ({ page }) => {
  const catalog = { ...aCatalog(ANIME, "Anime Archive", "Anime", false), unanchored: true };
  await setupApp(page, { role: "user", catalogs: [catalog] });
  await page.goto("/series");
  const alert = page.getByRole("alert").filter({ hasText: "Storage unavailable" });
  await expect(alert).toContainText("Anime Archive: storage location needs to be reconnected.");
  await expect(alert.getByRole("link", { name: "Manage catalogs" })).toHaveCount(0);
  await expect(page.getByRole("combobox", { name: "Filter series by catalog" })).toHaveCount(0);
  await page.route("**/api/proxy/api/catalogs", route => route.fulfill({ json: [{ ...catalog, online: true, unanchored: false }] }));
  await expect(alert).toHaveCount(0, { timeout: 12000 });
});

test("storage alert fits a narrow screen", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await setupApp(page, { role: "admin", catalogs: [aCatalog(MOVIES_HD, "Movies HD", "Movie", false)] });
  await page.goto("/movies");
  await expect(page.getByRole("alert").filter({ hasText: "Storage unavailable" })).toBeVisible();
  expect(await page.getByRole("main").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await page.screenshot({ path: "/tmp/media-server-storage-alert.png", fullPage: true });
});
