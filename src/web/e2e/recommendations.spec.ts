import { expect, test } from "@playwright/test";
import { setupApp } from "./support";

// The feed's job is to distinguish two things a card can be: something you can play right now, and
// something you would have to go and get. Everything else on this surface follows from that.

const feed = {
  items: [
    {
      kind: "Movie",
      tmdbId: "27205",
      title: "Inception",
      year: 2010,
      posterUrl: null,
      inLibrary: true,
      mediaItemId: "b7f3c2d1-0000-4000-8000-000000000001",
      reason: { kind: "rated-seed", detail: "Arrival", rating: 5 },
    },
    {
      kind: "Series",
      tmdbId: "95396",
      title: "Severance",
      year: 2022,
      posterUrl: null,
      inLibrary: false,
      mediaItemId: null,
      reason: null,
    },
  ],
};

test("the page separates what you hold from what you would have to find", async ({ page }) => {
  await setupApp(page, { recommendations: feed });
  await page.goto("/recommendations");

  await expect(page.getByText("Inception")).toBeVisible();
  // Only what you hold is marked, and with an icon: writing "not in library" on every other card would
  // spend the caption line to say nothing.
  await expect(page.getByTestId("rec-availability")).toHaveCount(1);
  await expect(page.getByTestId("rec-availability")).toHaveAttribute("aria-label", "In library");
  // Only the discovery offers Track; a held title links to its detail page instead.
  await expect(page.getByRole("button", { name: "Track" })).toHaveCount(1);
});

test("a card names its kind and year the way the library grids do", async ({ page }) => {
  await setupApp(page, { recommendations: feed });
  await page.goto("/recommendations");

  await expect(page.getByText("Movie · 2010")).toBeVisible();
  await expect(page.getByText("Series · 2022")).toBeVisible();
});


test("the availability filter narrows the feed", async ({ page }) => {
  await setupApp(page, { recommendations: feed });
  await page.goto("/recommendations");

  await page.getByRole("button", { name: "In library", exact: true }).click();
  await expect(page.getByText("Inception")).toBeVisible();
  await expect(page.getByText("Severance")).toHaveCount(0);

  await page.getByRole("button", { name: "Not in library", exact: true }).click();
  await expect(page.getByText("Severance")).toBeVisible();
  await expect(page.getByText("Inception")).toHaveCount(0);
});

test("hiding a card offers a way back", async ({ page }) => {
  await setupApp(page, { recommendations: feed });
  await page.goto("/recommendations");

  await page.getByRole("button", { name: "Hide Inception" }).click();

  // One click to hide means one click to undo — the toast is the whole safety net.
  await expect(page.getByRole("button", { name: "Undo" })).toBeVisible();
});


test("the popularity dial shows where it sits and saves where it is left", async ({ page }) => {
  // Unlike the source control this is always offered: it applies to the built-in engine, which every
  // instance has, and there is no defensible default for how much mainstream a viewer wants.
  await setupApp(page, { recommendations: { ...feed, popularityBias: 0.8, maxPopularityBias: 2 } });
  await page.goto("/recommendations");

  const dial = page.getByLabel("Popular to deep cuts");
  await expect(dial).toHaveValue("0.8");

  const saved = page.waitForRequest(
    (request) =>
      request.url().includes("/api/proxy/api/recommendations/popularity-bias") && request.method() === "PUT",
  );
  await dial.fill("1.5");
  await dial.blur();
  expect((await saved).postDataJSON()).toEqual({ popularityBias: 1.5 });
});

test("a card says why it is here, and says nothing when nothing could say", async ({ page }) => {
  // The reason is a third line on the page's grid, where there is room for one. On the Home row the
  // card keeps its deliberate two lines and the reason stays in the tooltip.
  await setupApp(page, { recommendations: feed });
  await page.goto("/recommendations");

  await expect(page.getByTestId("rec-reason")).toHaveCount(1);
  await expect(page.getByTestId("rec-reason")).toHaveText("Because you rated Arrival 5★");
});

test("an empty feed explains itself rather than showing a blank page", async ({ page }) => {
  await setupApp(page, { recommendations: { items: [] } });
  await page.goto("/recommendations");

  await expect(page.getByText(/Nothing to suggest yet/)).toBeVisible();
});

test("the home row appears only when there is something to recommend", async ({ page }) => {
  await setupApp(page, { recommendations: { items: [] } });
  await page.goto("/");
  await expect(page.getByText("Recommended for you")).toHaveCount(0);

  await setupApp(page, { recommendations: feed });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Recommended for you" })).toBeVisible();
  await expect(page.getByRole("link", { name: "See all" })).toBeVisible();
});

test("a held title links by media-item id, which is what the detail route resolves", async ({ page }) => {
  // The detail routes are declared {id:guid} and look up MediaItem.Id; linking by public id — a
  // deterministic hash — would not even match the route.
  await setupApp(page, { recommendations: feed });
  await page.goto("/recommendations");

  await expect(page.getByRole("link").filter({ has: page.locator("img, span") }).first()).toHaveAttribute(
    "href",
    "/movies/b7f3c2d1-0000-4000-8000-000000000001",
  );
});
