// Pure grouping behind the Watched calendar mode. Kept free of React so the grid and the mobile
// agenda can share one derivation — if they grouped independently their counts could disagree.
//
// Everything here groups by the *browser's* local day: the API returns raw UTC instants precisely so
// that a 00:30 play lands on the day the user actually watched it, daylight saving included.

import { endOfMonth, endOfWeek, format, startOfMonth, startOfWeek } from "date-fns";
import type { WatchHistoryCalendarEvent } from "@/lib/media-server";

/** The visible grid as UTC instants, for the calendar query. `toExclusive` is the day after the last. */
export function monthGridInstants(month: Date): { from: string; toExclusive: string } {
  const first = startOfWeek(startOfMonth(month), { weekStartsOn: 1 });
  const lastDay = endOfWeek(endOfMonth(month), { weekStartsOn: 1 });
  const toExclusive = new Date(lastDay.getFullYear(), lastDay.getMonth(), lastDay.getDate() + 1);
  return { from: first.toISOString(), toExclusive: toExclusive.toISOString() };
}

/** The local day an instant belongs to, as "yyyy-MM-dd". */
export function localDayKey(watchedAt: string): string {
  return format(new Date(watchedAt), "yyyy-MM-dd");
}

/**
 * One card in a day cell. A movie card covers every play of that movie that day; a series card covers
 * every episode of that series that day — a 10-episode binge is still one card, because the cell has
 * no room for more and the series is what the user remembers.
 *
 * Grouping is presentation only: {@link WatchedGroup.plays} keeps every underlying event, so the day
 * detail expands losslessly and no rewatch is ever hidden.
 */
export interface WatchedGroup {
  /** Stable within a day: the series id for episodes, the media-item id for movies. */
  key: string;
  kind: "Movie" | "Episode";
  title: string;
  posterUrl: string | null;
  /** Every play behind this card, in chronological order. */
  plays: WatchHistoryCalendarEvent[];
  /** Distinct episodes for a series card; 1 for a movie card. */
  episodeCount: number;
  firstWatchedAt: string;
  lastWatchedAt: string;
}

/** Groups one day's plays into cards: one per movie, one per series. */
export function groupDay(events: WatchHistoryCalendarEvent[]): WatchedGroup[] {
  const groups = new Map<string, WatchHistoryCalendarEvent[]>();
  for (const event of events) {
    // Episodes collapse to their series; a movie stands alone. An episode with no series id (an
    // orphan after a re-scan) falls back to its own id rather than joining a bogus bucket.
    const key = event.kind === "Episode" ? (event.seriesId ?? event.mediaItemId) : event.mediaItemId;
    const existing = groups.get(key);
    if (existing) {
      existing.push(event);
    } else {
      groups.set(key, [event]);
    }
  }

  return [...groups.entries()]
    .map(([key, plays]) => {
      const ordered = [...plays].sort((a, b) => a.watchedAt.localeCompare(b.watchedAt));
      const first = ordered[0];
      const isSeries = first.kind === "Episode";
      return {
        key,
        kind: first.kind,
        title: isSeries ? (first.seriesTitle ?? first.title) : first.title,
        posterUrl: first.posterUrl,
        plays: ordered,
        episodeCount: isSeries
          ? new Set(ordered.map((play) => play.mediaItemId)).size
          : 1,
        firstWatchedAt: ordered[0].watchedAt,
        lastWatchedAt: ordered[ordered.length - 1].watchedAt,
      } satisfies WatchedGroup;
    })
    .sort((a, b) => a.firstWatchedAt.localeCompare(b.firstWatchedAt));
}

/** Buckets events into local days, each already grouped into cards. */
export function groupWatchedByDay(events: WatchHistoryCalendarEvent[]): Map<string, WatchedGroup[]> {
  const byDay = new Map<string, WatchHistoryCalendarEvent[]>();
  for (const event of events) {
    const key = localDayKey(event.watchedAt);
    const existing = byDay.get(key);
    if (existing) {
      existing.push(event);
    } else {
      byDay.set(key, [event]);
    }
  }

  return new Map([...byDay.entries()].map(([day, dayEvents]) => [day, groupDay(dayEvents)]));
}

/** The kind filter the Watched toolbar offers. */
export type WatchedKindFilter = "all" | "movies" | "episodes";

export function filterEvents(
  events: WatchHistoryCalendarEvent[],
  filter: WatchedKindFilter,
): WatchHistoryCalendarEvent[] {
  if (filter === "all") {
    return events;
  }
  const kind = filter === "movies" ? "Movie" : "Episode";
  return events.filter((event) => event.kind === kind);
}

/**
 * The undated count matching the active filter. These rows are absent from `events` by design, so the
 * count has to come from the server's per-kind breakdown rather than from filtering.
 */
export function undatedFor(
  undated: { movies: number; episodes: number },
  filter: WatchedKindFilter,
): number {
  switch (filter) {
    case "movies":
      return undated.movies;
    case "episodes":
      return undated.episodes;
    case "all":
      return undated.movies + undated.episodes;
  }
}

/** The card's secondary line: an episode tally for a series, a rewatch tally for a movie. */
export function groupSubtitle(group: WatchedGroup): string {
  if (group.kind === "Episode") {
    return group.episodeCount === 1 ? "1 episode" : `${group.episodeCount} episodes`;
  }
  return group.plays.length > 1 ? `x${group.plays.length}` : formatTime(group.firstWatchedAt);
}

/** "19:42" in the browser's locale-independent 24-hour form, for dense grid text. */
export function formatTime(watchedAt: string): string {
  return format(new Date(watchedAt), "HH:mm");
}

/** "19:42–21:31" for a span, or a single time when the card covers one play. */
export function formatSpan(group: WatchedGroup): string {
  const start = formatTime(group.firstWatchedAt);
  const end = formatTime(group.lastWatchedAt);
  return start === end ? start : `${start}–${end}`;
}

/** "S2E3", or null when the numbering is unknown. */
export function episodeLabel(event: WatchHistoryCalendarEvent): string | null {
  return event.seasonNumber != null && event.episodeNumber != null
    ? `S${event.seasonNumber}E${event.episodeNumber}`
    : null;
}
