import { expect, test } from "@playwright/test";
import { aMovie, aRemovedTitle, aUserData, movieDetail, setupApp } from "./support";

// Removed titles are the record a deletion could not take with it: what the user watched, rated or
// marked. They live behind the library grid's own toggle — off by default, and absent entirely for a
// library that has none — because they are not things to watch.

test("the grid says nothing about removed titles when there are none", async ({ page }) => {
  await setupApp(page, { library: [aMovie("m1", "Arrival")], removedTitles: [] });

  await page.goto("/movies");

  await expect(page.getByRole("link", { name: /Arrival/ })).toBeVisible();
  await expect(page.getByText(/Show removed/)).toHaveCount(0);
});

test("removed titles stay out of the library until the toggle is on, and survive a reload", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    removedTitles: [aRemovedTitle("g1", "The Black Phone", { year: 2022, playCount: 1 })],
  });

  await page.goto("/movies");

  // Off by default: a deleted film is not part of the library and must not read as if it were.
  await expect(page.getByText("The Black Phone")).toHaveCount(0);

  await page.getByRole("checkbox", { name: /Show removed/ }).click();

  await expect(page.getByRole("heading", { name: "Removed" })).toBeVisible();
  await expect(page.getByRole("link", { name: /The Black Phone/ })).toBeVisible();

  // In the URL, like the catalog filter: the view survives a refresh and can be sent to someone else.
  await expect(page).toHaveURL(/removed=1/);
  await page.reload();
  await expect(page.getByRole("link", { name: /The Black Phone/ })).toBeVisible();
});

test("a removed title's card opens what is left of it, and clearing a mark is offered per mark", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    detail: { g1: { ...movieDetail("g1", "The Black Phone"), removedAt: "2026-08-01T00:00:00Z", catalogId: null, userData: aUserData({ userRating: 4, isFavorite: true }) } },
    library: [aMovie("m1", "Arrival")],
    removedTitles: [
      aRemovedTitle("g1", "The Black Phone", {
        year: 2022,
        playCount: 1,
        lastWatchedAt: "2026-08-03T20:00:00Z",
        userRating: 4,
        isFavorite: true,
      }),
    ],
  });

  await page.goto("/movies?removed=1");
  await page.getByRole("link", { name: /The Black Phone/ }).click();

  await expect(page).toHaveURL(/\/movies\/g1\?removed=1/);
  await expect(page.getByText("Removed from library", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Media", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Clear your rating" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove favorite" })).toBeVisible();
  await page.getByRole("button", { name: "Clear your rating" }).click();
  const cleared = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/g1/rating") && request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Clear rating", exact: true }).click();
  await cleared;
});

test("only an admin is offered the permanent delete", async ({ page }) => {
  await setupApp(page, {
    role: "user",
    detail: { g1: { ...movieDetail("g1", "The Black Phone"), removedAt: "2026-08-01T00:00:00Z", userData: aUserData({ userRating: 4 }) } },
    library: [aMovie("m1", "Arrival")],
    removedTitles: [aRemovedTitle("g1", "The Black Phone", { userRating: 4 })],
  });

  await page.goto("/movies?removed=1");
  await page.getByRole("link", { name: /The Black Phone/ }).click();

  await expect(page.getByRole("button", { name: "Clear your rating" })).toBeVisible();
  await page.getByRole("button", { name: "More actions" }).click();
  await expect(page.getByRole("menuitem", { name: /Delete permanently/ })).toHaveCount(0);
});

test("a series ghost belongs to the series grid, not the movies one", async ({ page }) => {
  await setupApp(page, {
    library: [],
    removedTitles: [
      aRemovedTitle("g1", "The Black Phone", { kind: "Movie" }),
      aRemovedTitle("g2", "Dark", { kind: "Series" }),
    ],
  });

  await page.goto("/movies?removed=1");
  await expect(page.getByRole("link", { name: /The Black Phone/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /Dark/ })).toHaveCount(0);

  await page.goto("/series?removed=1");
  await expect(page.getByRole("button", { name: /Dark/ })).toBeVisible();
  await expect(page.getByRole("link", { name: /The Black Phone/ })).toHaveCount(0);
});
