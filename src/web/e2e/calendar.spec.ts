import { expect, test } from "@playwright/test";
import { setupApp } from "./support";

// The calendar's two modes share one shell, so the risks are structural: that switching modes loses
// the month, that a shared link opens the wrong view, or that the watched work disturbed releases.

const aRelease = {
  releaseId: "r1",
  entryId: "e1",
  trackedTitleId: "t1",
  kind: "Movie",
  title: "Dune: Part Three",
  posterUrl: null,
  type: "Theatrical",
  date: "2026-07-15",
  previousDate: null,
  season: null,
  episode: null,
  note: null,
  hasReminder: false,
  inLibrary: false,
};

const watchedHistory = {
  events: [
    {
      entryId: "p1",
      watchedAt: "2026-07-08T19:42:00.000Z",
      mediaItemId: "ep-1",
      publicId: "ep-1",
      kind: "Episode",
      title: "Episode 1",
      posterUrl: null,
      seriesId: "series-1",
      seriesTitle: "Severance",
      seasonNumber: 2,
      episodeNumber: 1,
      origin: "LocalPlayback",
    },
    {
      entryId: "p2",
      watchedAt: "2026-07-08T20:37:00.000Z",
      mediaItemId: "ep-2",
      publicId: "ep-2",
      kind: "Episode",
      title: "Episode 2",
      posterUrl: null,
      seriesId: "series-1",
      seriesTitle: "Severance",
      seasonNumber: 2,
      episodeNumber: 2,
      origin: "LocalPlayback",
    },
    {
      entryId: "p3",
      watchedAt: "2026-07-10T21:00:00.000Z",
      mediaItemId: "movie-1",
      publicId: "movie-1",
      kind: "Movie",
      title: "Arrival",
      posterUrl: null,
      seriesId: null,
      seriesTitle: null,
      seasonNumber: null,
      episodeNumber: null,
      origin: "LocalPlayback",
    },
  ],
  undated: { movies: 3, episodes: 5 },
  latestWatchedAt: "2026-07-10T21:00:00.000Z",
};

test("releases is the default mode and keeps its own actions", async ({ page }) => {
  await setupApp(page, { releaseCalendar: [aRelease] });
  await page.goto("/calendar?month=2026-07");

  await expect(page.getByRole("tab", { name: "releases" })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByText("Dune: Part Three")).toBeVisible();
  await expect(page.getByRole("button", { name: "Add title" })).toBeVisible();
});

test("switching to watched keeps the month and swaps the toolbar", async ({ page }) => {
  await setupApp(page, { releaseCalendar: [aRelease], watchHistoryCalendar: watchedHistory });
  // Deliberately not the current month: the href omits the month when it is today's, so a current
  // month would make the preservation assertion vacuous.
  await page.goto("/calendar?month=2026-03");

  await page.getByRole("tab", { name: "watched" }).click();

  await expect(page).toHaveURL(/view=watched/);
  await expect(page).toHaveURL(/month=2026-03/);
  // Release-only actions are gone; the watched filters take their place.
  await expect(page.getByRole("button", { name: "Add title" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Movies", exact: true })).toBeVisible();
});

test("a watched deep link opens that view directly", async ({ page }) => {
  await setupApp(page, { watchHistoryCalendar: watchedHistory });
  await page.goto("/calendar?view=watched&month=2026-07");

  await expect(page.getByRole("tab", { name: "watched" })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByTestId("calendar-grid").getByText("Severance")).toBeVisible();
});

test("a binge of one series is a single card that expands to every episode", async ({ page }) => {
  await setupApp(page, { watchHistoryCalendar: watchedHistory });
  await page.goto("/calendar?view=watched&month=2026-07");

  // Two episodes on one day render one card labelled by the series.
  const grid = page.getByTestId("calendar-grid");
  const card = grid.getByRole("button", { name: /Severance/ });
  await expect(card).toHaveCount(1);
  await expect(grid.getByText("2 episodes")).toBeVisible();

  await card.click();

  // The day detail unwinds the grouping: both plays with their exact times.
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByText("S2E1 · Episode 1")).toBeVisible();
  await expect(dialog.getByText("S2E2 · Episode 2")).toBeVisible();
});

test("the undated count follows the kind filter", async ({ page }) => {
  await setupApp(page, { watchHistoryCalendar: watchedHistory });
  await page.goto("/calendar?view=watched&month=2026-07");

  await expect(page.getByRole("button", { name: "Undated 8" })).toBeVisible();

  await page.getByRole("button", { name: "Movies", exact: true }).click();
  await expect(page.getByRole("button", { name: "Undated 3" })).toBeVisible();

  await page.getByRole("button", { name: "Episodes", exact: true }).click();
  await expect(page.getByRole("button", { name: "Undated 5" })).toBeVisible();
});

test("an empty month offers a jump to the last watched one", async ({ page }) => {
  await setupApp(page, {
    watchHistoryCalendar: { events: [], undated: { movies: 0, episodes: 0 }, latestWatchedAt: "2026-03-02T18:00:00.000Z" },
  });
  await page.goto("/calendar?view=watched&month=2026-07");

  await expect(page.getByText("Nothing watched this month.")).toBeVisible();
  const jump = page.getByRole("button", { name: "Jump to last watched month" });
  await jump.click();

  // The jump is client-side, so this waits on hydration as well as navigation — under parallel
  // workers that can outlast the default expect timeout.
  await expect(page).toHaveURL(/month=2026-03/, { timeout: 15_000 });
});

test("a phone gets a date-grouped agenda instead of seven columns", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await setupApp(page, { watchHistoryCalendar: watchedHistory });
  await page.goto("/calendar?view=watched&month=2026-07");

  const agenda = page.getByTestId("calendar-agenda");
  await expect(agenda).toBeVisible();
  await expect(page.getByTestId("calendar-grid")).toBeHidden();

  // The same series-per-day card as the grid, with the day's real play count beside the date.
  await expect(agenda.getByText("2 episodes")).toBeVisible();
  await expect(agenda.getByText("2 plays")).toBeVisible();
  await expect(agenda.getByText("1 play")).toBeVisible();
});
