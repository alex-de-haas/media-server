import { expect, test } from "@playwright/test";
import { aMovie, aRemovedTitle, setupApp } from "./support";

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
  await expect(page.getByRole("button", { name: /The Black Phone/ })).toBeVisible();

  // In the URL, like the catalog filter: the view survives a refresh and can be sent to someone else.
  await expect(page).toHaveURL(/removed=1/);
  await page.reload();
  await expect(page.getByRole("button", { name: /The Black Phone/ })).toBeVisible();
});

test("a removed title's card opens what is left of it, and clearing a mark is offered per mark", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
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
  await page.getByRole("button", { name: /The Black Phone/ }).click();

  // A ghost has no detail page — this dialog is the whole of it: the marks, and the ways to drop them.
  await expect(page.getByRole("dialog")).toContainText("4/5");
  await expect(page.getByRole("dialog")).toContainText("Favorite");
  await expect(page.getByRole("button", { name: "Unfavorite" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Clear rating" })).toBeVisible();

  const cleared = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/removed/g1/rating") && request.method() === "DELETE",
  );
  await page.getByRole("button", { name: "Clear rating" }).click();
  await cleared;
});

test("only an admin is offered the permanent delete", async ({ page }) => {
  await setupApp(page, {
    role: "user",
    library: [aMovie("m1", "Arrival")],
    removedTitles: [aRemovedTitle("g1", "The Black Phone", { userRating: 4 })],
  });

  await page.goto("/movies?removed=1");
  await page.getByRole("button", { name: /The Black Phone/ }).click();

  await expect(page.getByRole("button", { name: "Clear rating" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Delete permanently" })).toHaveCount(0);
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
  await expect(page.getByRole("button", { name: /The Black Phone/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /Dark/ })).toHaveCount(0);

  await page.goto("/series?removed=1");
  await expect(page.getByRole("button", { name: /Dark/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /The Black Phone/ })).toHaveCount(0);
});
