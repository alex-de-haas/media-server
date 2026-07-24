import { describe, expect, it } from "vitest";
import type { WatchHistoryCalendarEvent } from "@/lib/media-server";
import {
  episodeLabel,
  filterEvents,
  formatSpan,
  groupDay,
  groupSubtitle,
  groupWatchedByDay,
  localDayKey,
  monthGridInstants,
  undatedFor,
} from "@/lib/watch-history-calendar";

function play(overrides: Partial<WatchHistoryCalendarEvent> = {}): WatchHistoryCalendarEvent {
  return {
    entryId: crypto.randomUUID(),
    watchedAt: "2026-07-10T20:00:00.000Z",
    mediaItemId: "movie-1",
    publicId: null,
    kind: "Movie",
    title: "Arrival",
    posterUrl: null,
    seriesId: null,
    seriesTitle: null,
    seasonNumber: null,
    episodeNumber: null,
    origin: "LocalPlayback",
    ...overrides,
  };
}

function episode(seriesId: string, mediaItemId: string, watchedAt: string, number: number) {
  return play({
    kind: "Episode",
    mediaItemId,
    watchedAt,
    title: `Episode ${number}`,
    seriesId,
    seriesTitle: "Severance",
    seasonNumber: 2,
    episodeNumber: number,
    posterUrl: "https://img/severance.jpg",
  });
}

describe("groupDay", () => {
  it("collapses a whole binge of one series into a single card", () => {
    // The decided rule: the cell has no room for ten chips, and the series is the unit the user
    // remembers. Ten episodes, one card.
    const plays = Array.from({ length: 10 }, (_, index) =>
      episode("series-1", `ep-${index}`, `2026-07-08T${String(10 + index).padStart(2, "0")}:00:00.000Z`, index + 1),
    );

    const groups = groupDay(plays);

    expect(groups).toHaveLength(1);
    expect(groups[0].title).toBe("Severance");
    expect(groups[0].episodeCount).toBe(10);
    // Lossless: every underlying play is still reachable for the day detail.
    expect(groups[0].plays).toHaveLength(10);
  });

  it("keeps separate series apart", () => {
    const groups = groupDay([
      episode("series-1", "ep-1", "2026-07-08T19:00:00.000Z", 1),
      { ...episode("series-2", "ep-9", "2026-07-08T21:00:00.000Z", 4), seriesTitle: "Andor" },
    ]);

    expect(groups.map((group) => group.title)).toEqual(["Severance", "Andor"]);
  });

  it("counts a rewatch of one movie as one card holding both plays", () => {
    const groups = groupDay([
      play({ watchedAt: "2026-07-10T14:00:00.000Z" }),
      play({ watchedAt: "2026-07-10T22:00:00.000Z" }),
    ]);

    expect(groups).toHaveLength(1);
    expect(groups[0].plays).toHaveLength(2);
    expect(groupSubtitle(groups[0])).toBe("x2");
  });

  it("does not merge distinct movies", () => {
    const groups = groupDay([
      play({ mediaItemId: "movie-1", title: "Arrival" }),
      play({ mediaItemId: "movie-2", title: "Dune" }),
    ]);

    expect(groups).toHaveLength(2);
  });

  it("counts a rewatched episode once toward the episode tally", () => {
    // Two plays, one episode: the card says "1 episode" but keeps both timestamps.
    const groups = groupDay([
      episode("series-1", "ep-1", "2026-07-08T19:00:00.000Z", 1),
      episode("series-1", "ep-1", "2026-07-08T23:00:00.000Z", 1),
    ]);

    expect(groups[0].episodeCount).toBe(1);
    expect(groups[0].plays).toHaveLength(2);
    expect(groupSubtitle(groups[0])).toBe("1 episode");
  });

  it("falls back to the item id when an episode has lost its series", () => {
    const orphan = { ...episode("series-1", "ep-1", "2026-07-08T19:00:00.000Z", 1), seriesId: null };

    const groups = groupDay([orphan]);

    expect(groups).toHaveLength(1);
    expect(groups[0].key).toBe("ep-1");
  });

  it("orders cards by their first play", () => {
    const groups = groupDay([
      play({ mediaItemId: "late", title: "Late", watchedAt: "2026-07-10T23:00:00.000Z" }),
      play({ mediaItemId: "early", title: "Early", watchedAt: "2026-07-10T09:00:00.000Z" }),
    ]);

    expect(groups.map((group) => group.title)).toEqual(["Early", "Late"]);
  });
});

describe("groupWatchedByDay", () => {
  it("buckets plays into local days", () => {
    const byDay = groupWatchedByDay([
      play({ watchedAt: "2026-07-10T12:00:00.000Z" }),
      play({ watchedAt: "2026-07-11T12:00:00.000Z", mediaItemId: "movie-2" }),
    ]);

    expect([...byDay.keys()].sort()).toEqual(["2026-07-10", "2026-07-11"]);
  });

  it("uses the browser's local day, not the UTC one", () => {
    // Whatever the runner's zone, the key must agree with local formatting of the same instant.
    const instant = "2026-07-10T23:30:00.000Z";
    const byDay = groupWatchedByDay([play({ watchedAt: instant })]);

    expect([...byDay.keys()]).toEqual([localDayKey(instant)]);
  });
});

describe("monthGridInstants", () => {
  it("spans full Monday-first weeks and ends exclusively", () => {
    const { from, toExclusive } = monthGridInstants(new Date(2026, 6, 14));

    const start = new Date(from);
    const end = new Date(toExclusive);
    expect(start.getDay()).toBe(1);
    // The exclusive bound is the Monday after the final Sunday.
    expect(end.getDay()).toBe(1);
    expect(end.getTime()).toBeGreaterThan(start.getTime());
    // Six weeks at most, which is why the server caps the range at 62 days.
    expect((end.getTime() - start.getTime()) / 86_400_000).toBeLessThanOrEqual(42);
  });
});

describe("filters", () => {
  const events = [play(), episode("series-1", "ep-1", "2026-07-08T19:00:00.000Z", 1)];

  it("narrows events to one kind", () => {
    expect(filterEvents(events, "all")).toHaveLength(2);
    expect(filterEvents(events, "movies").every((event) => event.kind === "Movie")).toBe(true);
    expect(filterEvents(events, "episodes").every((event) => event.kind === "Episode")).toBe(true);
  });

  it("reports the undated count matching the active filter", () => {
    const undated = { movies: 8, episodes: 4 };

    expect(undatedFor(undated, "all")).toBe(12);
    expect(undatedFor(undated, "movies")).toBe(8);
    expect(undatedFor(undated, "episodes")).toBe(4);
  });
});

describe("labels", () => {
  it("shows a span for a binge and a single time for one play", () => {
    const binge = groupDay([
      episode("series-1", "ep-1", "2026-07-08T19:42:00.000Z", 1),
      episode("series-1", "ep-2", "2026-07-08T21:31:00.000Z", 2),
    ])[0];
    const single = groupDay([play({ watchedAt: "2026-07-10T20:00:00.000Z" })])[0];

    expect(formatSpan(binge)).toContain("–");
    expect(formatSpan(single)).not.toContain("–");
  });

  it("builds an episode code only when both numbers are known", () => {
    expect(episodeLabel(episode("s", "e", "2026-07-08T19:00:00.000Z", 3))).toBe("S2E3");
    expect(episodeLabel(play())).toBeNull();
  });
});
