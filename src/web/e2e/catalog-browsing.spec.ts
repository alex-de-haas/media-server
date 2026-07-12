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

  await page.goto("/catalogs");
  await page.getByRole("button", { name: "Catalog actions" }).click();
  await page.getByRole("menuitem", { name: "Browse media" }).click();

  await expect(page).toHaveURL(`/series?catalog=${ANIME}`);
});
