import { expect, test } from "@playwright/test";
import { aTitlePreview, setupApp } from "./support";

// The preview exists so a title the instance does not hold can still be understood before acting on it.
// What matters is that it opens where a title is only a poster, says what the title is, and offers only
// the actions that make sense for something nobody here holds.

const feed = {
  items: [
    {
      kind: "Movie",
      tmdbId: "27205",
      title: "Inception",
      year: 2010,
      posterUrl: null,
      inLibrary: false,
      mediaItemId: null,
      sources: ["library"],
    },
  ],
  sources: [{ key: "library", displayName: "Your library" }],
  selectedSources: ["library"],
};

const preview = { "27205": aTitlePreview("27205", "Inception") };

// The same title as the feed's discovery, already on the user's calendar.
const aTrackedInception = {
  id: "w1",
  trackedTitleId: "t1",
  kind: "Movie",
  title: "Inception",
  year: 2010,
  posterUrl: null,
  provider: "tmdb",
  providerId: "27205",
  productionStatus: null,
  inLibrary: false,
  libraryItemId: null,
  monitorScope: null,
  monitoredSeasons: [],
  regionOverride: null,
  note: null,
  nextRelease: null,
  hasDates: false,
  libraryGap: null,
  reminders: [],
};

test("a discovery poster opens the preview and it says what the title is", async ({ page }) => {
  await setupApp(page, { recommendations: feed, titlePreview: preview });
  await page.goto("/recommendations");

  await page.getByRole("button", { name: "Details for Inception" }).click();

  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("heading", { name: "Inception" })).toBeVisible();
  await expect(dialog.getByText("A thief who steals corporate secrets through dream-sharing technology.")).toBeVisible();
  await expect(dialog.getByText("2010 · 2h 28m · Science Fiction, Action")).toBeVisible();
  await expect(dialog.getByText("PG-13")).toBeVisible();
  await expect(dialog.getByText("8.4")).toBeVisible();
  await expect(dialog.getByText("Christopher Nolan")).toBeVisible();
  await expect(dialog.getByText("Leonardo DiCaprio")).toBeVisible();
});

test("the preview offers tracking, never playback", async ({ page }) => {
  await setupApp(page, { recommendations: feed, titlePreview: preview });
  await page.goto("/recommendations");
  await page.getByRole("button", { name: "Details for Inception" }).click();

  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("button", { name: "Track / remind me" })).toBeVisible();
  await expect(dialog.getByRole("link", { name: "Open in library" })).toHaveCount(0);
});

test("a title can be dismissed from the preview, which closes it", async ({ page }) => {
  await setupApp(page, { recommendations: feed, titlePreview: preview });
  await page.goto("/recommendations");
  await page.getByRole("button", { name: "Details for Inception" }).click();

  await page.getByRole("dialog").getByRole("button", { name: "Not interested" }).click();

  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByText("Hid Inception")).toBeVisible();
});

test("a held title links to its detail page instead of offering to track it blind", async ({ page }) => {
  await setupApp(page, {
    recommendations: feed,
    titlePreview: {
      "27205": aTitlePreview("27205", "Inception", { inLibrary: true, mediaItemId: "b7f3c2d1-0000-4000-8000-000000000001" }),
    },
  });
  await page.goto("/recommendations");
  await page.getByRole("button", { name: "Details for Inception" }).click();

  await expect(page.getByRole("dialog").getByRole("link", { name: "Open in library" })).toHaveAttribute(
    "href",
    "/movies/b7f3c2d1-0000-4000-8000-000000000001",
  );
});

test("a title the provider cannot answer for still shows what the card knew", async ({ page }) => {
  await setupApp(page, { recommendations: feed }); // no titlePreview → 404
  await page.goto("/recommendations");
  await page.getByRole("button", { name: "Details for Inception" }).click();

  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("heading", { name: "Inception" })).toBeVisible();
  await expect(dialog.getByText("Couldn’t load this.")).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Retry" })).toBeVisible();
});

test("a tracked series is not mistaken for the movie sharing its TMDb id", async ({ page }) => {
  await setupApp(page, {
    recommendations: feed, // the movie 27205
    titlePreview: preview,
    watchlist: [
      {
        id: "w1",
        trackedTitleId: "t1",
        kind: "Series", // same id, the other id space
        title: "Something Else",
        year: 2019,
        posterUrl: null,
        provider: "tmdb",
        providerId: "27205",
        productionStatus: null,
        inLibrary: false,
        libraryItemId: null,
        monitorScope: null,
        monitoredSeasons: [],
        regionOverride: null,
        note: null,
        nextRelease: null,
        hasDates: false,
        libraryGap: null,
        reminders: [],
      },
    ],
  });
  await page.goto("/recommendations");
  await page.getByRole("button", { name: "Details for Inception" }).click();

  await expect(page.getByRole("dialog").getByRole("button", { name: "Track / remind me" })).toBeVisible();
  await expect(page.getByRole("dialog").getByRole("button", { name: "Tracked" })).toHaveCount(0);
});

test("a search candidate can be checked before it is tracked", async ({ page }) => {
  await setupApp(page, {
    titlePreview: preview,
    metadataSearch: [{ reference: { provider: "tmdb", id: "27205" }, title: "Inception", year: 2010, score: 1, posterUrl: null }],
  });
  await page.goto("/calendar?month=2026-07");

  await page.getByRole("button", { name: "Add title" }).click();
  await page.getByRole("textbox", { name: "Title" }).fill("Inception");
  await page.getByRole("button", { name: "Search" }).click();
  await page.getByRole("button", { name: /Inception/ }).click();

  // The preview takes the screen from the search dialog — one modal at a time — and carries the Track
  // action the results were for. Escape has to reach it, not an overlay left underneath.
  const previewDialog = page.getByRole("dialog").filter({ hasText: "A thief who steals corporate secrets" });
  await expect(previewDialog).toBeVisible();
  await expect(previewDialog.getByRole("button", { name: "Track / remind me" })).toBeVisible();

  await page.keyboard.press("Escape");
  await expect(previewDialog).toBeHidden();
});

test("a tracked title opens its preview from the tracked drawer", async ({ page }) => {
  await setupApp(page, {
    titlePreview: preview,
    watchlist: [aTrackedInception],
  });
  await page.goto("/calendar?month=2026-07");

  await page.getByRole("button", { name: "Tracked titles" }).click();
  await page.getByRole("button", { name: /Inception/ }).click();

  await expect(page.getByRole("dialog").getByText("A thief who steals corporate secrets through dream-sharing technology.")).toBeVisible();
});

// A preview rendered inside the drawer's root belongs to the drawer as far as base-ui is concerned, and
// nothing outside it can dismiss it — a dialog with no way out.
test("the preview opened over the drawer can be closed every ordinary way", async ({ page }) => {
  await setupApp(page, { titlePreview: preview, watchlist: [aTrackedInception] });
  await page.goto("/calendar?month=2026-07");

  const overview = page.getByText("A thief who steals corporate secrets through dream-sharing technology.");
  // Reopening goes through the drawer each time, since opening the preview hands the screen over to it.
  // Waiting for it to be open matters: Escape and outside clicks are routed by focus.
  const openPreview = async () => {
    await page.getByRole("button", { name: "Tracked titles" }).click();
    await page.getByRole("button", { name: /Inception/ }).first().click();
    await expect(overview).toBeVisible();
  };

  await openPreview();
  await page.getByRole("button", { name: "Close" }).first().click();
  await expect(overview).toBeHidden();

  await openPreview();
  await page.keyboard.press("Escape");
  await expect(overview).toBeHidden();

  await openPreview();
  // Derived from the dialog's own box rather than a fixed point, so a change in its size or the viewport
  // cannot quietly move the click inside it.
  const box = await page.getByRole("dialog").boundingBox();
  await page.mouse.click(Math.max(4, (box?.x ?? 100) / 2), Math.max(4, (box?.y ?? 100) / 2));
  await expect(overview).toBeHidden();
});
