// Pure helpers behind the /calendar month grid and the reminder dialog labels. Kept free of React so
// the grid derivation and label formatting are unit-testable (see calendar.test.ts).

import { eachDayOfInterval, endOfMonth, endOfWeek, format, startOfMonth, startOfWeek } from "date-fns";
import type { CalendarEvent, ReleaseType } from "@/lib/watchlist";

/** Monday-first weekday header, matching the grid produced by {@link monthGridDays}. */
export const WEEKDAY_LABELS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] as const;

/** Which question the calendar is answering: what is coming, or what was watched. */
export type CalendarMode = "releases" | "watched";

/**
 * Releases is the default so an existing `/calendar` link keeps its meaning. Anything unrecognized
 * falls back rather than erroring — a hand-edited URL should show a calendar, not a crash.
 */
export function parseCalendarMode(value: string | null | undefined): CalendarMode {
  return value === "watched" ? "watched" : "releases";
}

/** The month a `?month=yyyy-MM` param selects; an absent or malformed value means "this month". */
export function parseMonthParam(value: string | null | undefined, today: Date = new Date()): Date {
  const match = /^(\d{4})-(\d{2})$/.exec(value ?? "");
  if (!match) {
    return startOfMonth(today);
  }
  const year = Number(match[1]);
  const month = Number(match[2]);
  if (month < 1 || month > 12) {
    return startOfMonth(today);
  }
  return new Date(year, month - 1, 1);
}

/** The `?month=` wire format: "yyyy-MM" in local time. */
export function toMonthParam(month: Date): string {
  return format(month, "yyyy-MM");
}

/**
 * A shareable calendar URL. Defaults are omitted so the common case stays `/calendar` — the state is
 * explicit in the link when it differs, and never persisted behind the user's back.
 */
export function calendarHref(mode: CalendarMode, month: Date, today: Date = new Date()): string {
  const params = new URLSearchParams();
  if (mode !== "releases") {
    params.set("view", mode);
  }
  if (toMonthParam(month) !== toMonthParam(today)) {
    params.set("month", toMonthParam(month));
  }
  const query = params.toString();
  return query ? `/calendar?${query}` : "/calendar";
}

/**
 * Every day cell of the month grid: full Monday-first weeks covering the given month, so the length is
 * always a multiple of 7 and the first/last cells may belong to the adjacent months.
 */
export function monthGridDays(month: Date): Date[] {
  return eachDayOfInterval({
    start: startOfWeek(startOfMonth(month), { weekStartsOn: 1 }),
    end: endOfWeek(endOfMonth(month), { weekStartsOn: 1 }),
  });
}

/** The grid's date-range bounds as "yyyy-MM-dd", for the calendar query. */
export function monthGridRange(month: Date): { from: string; to: string } {
  const days = monthGridDays(month);
  return { from: toDateKey(days[0]), to: toDateKey(days[days.length - 1]) };
}

/** A calendar date key ("yyyy-MM-dd") in local time — the backend's DateOnly wire format. */
export function toDateKey(date: Date): string {
  return format(date, "yyyy-MM-dd");
}

/** Parses a "yyyy-MM-dd" key as a local date (never UTC-shifted). */
export function fromDateKey(key: string): Date {
  const [year, month, day] = key.split("-").map(Number);
  return new Date(year, month - 1, day);
}

/** Buckets events by their date key; events keep the backend's date-then-title order within a day. */
export function groupEventsByDay(events: CalendarEvent[]): Map<string, CalendarEvent[]> {
  const byDay = new Map<string, CalendarEvent[]>();
  for (const event of events) {
    const existing = byDay.get(event.date);
    if (existing) {
      existing.push(event);
    } else {
      byDay.set(event.date, [event]);
    }
  }
  return byDay;
}

/** The reminder dialog's lead choices. */
export const LEAD_OPTIONS = [
  { value: 0, label: "On the day" },
  { value: 1, label: "1 day before" },
  { value: 2, label: "2 days before" },
  { value: 7, label: "A week before" },
] as const;

export function leadLabel(days: number): string {
  const preset = LEAD_OPTIONS.find((option) => option.value === days);
  return preset ? preset.label : `${days} days before`;
}

export function releaseTypeLabel(type: ReleaseType): string {
  switch (type) {
    case "Premiere":
      return "Premiere";
    case "Theatrical":
      return "Theatrical";
    case "Digital":
      return "Digital";
    case "EpisodeAir":
      return "Episode";
  }
}

/** "S4E2" — or null when either half is missing (a movie event). */
export function episodeCode(season: number | null, episode: number | null): string | null {
  return season != null && episode != null ? `S${season}E${episode}` : null;
}

/** The short label shown on a grid chip: episode code for series airs, type label otherwise. */
export function eventChipLabel(event: Pick<CalendarEvent, "type" | "season" | "episode">): string {
  return event.type === "EpisodeAir"
    ? (episodeCode(event.season, event.episode) ?? "Episode")
    : releaseTypeLabel(event.type);
}

/** "Aug 14, 2026" for a "yyyy-MM-dd" key. */
export function formatDateKey(key: string): string {
  return format(fromDateKey(key), "MMM d, yyyy");
}
