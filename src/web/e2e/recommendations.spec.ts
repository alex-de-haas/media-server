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
      mediaItemId: "m1",
      publicId: "m1",
      sources: ["library"],
    },
    {
      kind: "Series",
      tmdbId: "95396",
      title: "Severance",
      year: 2022,
      posterUrl: null,
      inLibrary: false,
      mediaItemId: null,
      publicId: null,
      sources: ["library", "trakt"],
    },
  ],
  sources: [
    { key: "library", displayName: "Your library" },
    { key: "trakt", displayName: "Trakt" },
  ],
  selectedSources: ["library", "trakt"],
};

test("the page separates what you hold from what you would have to find", async ({ page }) => {
  await setupApp(page, { recommendations: feed });
  await page.goto("/recommendations");

  await expect(page.getByText("Inception")).toBeVisible();
  // Scoped to the card labels: the filter buttons carry the same words.
  await expect(page.getByTestId("rec-availability").filter({ hasText: /^In library$/ })).toHaveCount(1);
  await expect(page.getByTestId("rec-availability").filter({ hasText: "Not in library" })).toHaveCount(1);
  // Only the discovery offers Track; a held title links to its detail page instead.
  await expect(page.getByRole("button", { name: "Track" })).toHaveCount(1);
});

test("a title both engines agreed on says so", async ({ page }) => {
  await setupApp(page, { recommendations: feed });
  await page.goto("/recommendations");

  await expect(page.getByText("Both")).toHaveCount(1);
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

test("the source control appears only when there is a second source", async ({ page }) => {
  await setupApp(page, {
    recommendations: { ...feed, sources: [{ key: "library", displayName: "Your library" }], selectedSources: ["library"] },
  });
  await page.goto("/recommendations");

  await expect(page.getByRole("group", { name: "Sources" })).toHaveCount(0);
});

test("an empty feed explains itself rather than showing a blank page", async ({ page }) => {
  await setupApp(page, { recommendations: { items: [], sources: [], selectedSources: [] } });
  await page.goto("/recommendations");

  await expect(page.getByText(/Nothing to suggest yet/)).toBeVisible();
});

test("the home row appears only when there is something to recommend", async ({ page }) => {
  await setupApp(page, { recommendations: { items: [], sources: [], selectedSources: [] } });
  await page.goto("/");
  await expect(page.getByText("Recommended for you")).toHaveCount(0);

  await setupApp(page, { recommendations: feed });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Recommended for you" })).toBeVisible();
  await expect(page.getByRole("link", { name: "See all" })).toBeVisible();
});
