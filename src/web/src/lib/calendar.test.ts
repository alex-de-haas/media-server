import { describe, expect, it } from "vitest";
import {
  episodeCode,
  eventChipLabel,
  formatDateKey,
  fromDateKey,
  groupEventsByDay,
  leadLabel,
  monthGridDays,
  monthGridRange,
  toDateKey,
} from "@/lib/calendar";
import type { CalendarEvent } from "@/lib/watchlist";

describe("monthGridDays", () => {
  it("produces full Monday-first weeks covering the month", () => {
    // July 2026 starts on a Wednesday and ends on a Friday.
    const days = monthGridDays(new Date(2026, 6, 14));

    expect(days.length % 7).toBe(0);
    expect(days[0].getDay()).toBe(1); // Monday
    expect(days[days.length - 1].getDay()).toBe(0); // Sunday
    expect(toDateKey(days[0])).toBe("2026-06-29"); // leading June days pad the first week
    expect(toDateKey(days[days.length - 1])).toBe("2026-08-02"); // trailing August days pad the last
  });

  it("covers a month that already starts on Monday without a leading pad week", () => {
    // June 2026 starts on a Monday.
    const days = monthGridDays(new Date(2026, 5, 1));
    expect(toDateKey(days[0])).toBe("2026-06-01");
    expect(days).toHaveLength(35);
  });

  it("exposes the grid bounds for the calendar query", () => {
    expect(monthGridRange(new Date(2026, 6, 1))).toEqual({ from: "2026-06-29", to: "2026-08-02" });
  });
});

describe("date keys", () => {
  it("round-trips a local calendar date without timezone shifts", () => {
    const date = fromDateKey("2026-07-14");
    expect(date.getFullYear()).toBe(2026);
    expect(date.getMonth()).toBe(6);
    expect(date.getDate()).toBe(14);
    expect(toDateKey(date)).toBe("2026-07-14");
  });

  it("formats a key for display", () => {
    expect(formatDateKey("2026-08-14")).toBe("Aug 14, 2026");
  });
});

describe("groupEventsByDay", () => {
  const event = (date: string, title: string): CalendarEvent => ({
    releaseId: title,
    entryId: "e",
    trackedTitleId: "t",
    kind: "Movie",
    title,
    posterUrl: null,
    type: "Digital",
    date,
    previousDate: null,
    season: null,
    episode: null,
    note: null,
    hasReminder: false,
    inLibrary: false,
  });

  it("buckets by date and keeps within-day order", () => {
    const grouped = groupEventsByDay([event("2026-07-14", "A"), event("2026-07-15", "B"), event("2026-07-14", "C")]);
    expect([...grouped.keys()]).toEqual(["2026-07-14", "2026-07-15"]);
    expect(grouped.get("2026-07-14")!.map((entry) => entry.title)).toEqual(["A", "C"]);
  });
});

describe("labels", () => {
  it("labels leads with the dialog presets", () => {
    expect(leadLabel(0)).toBe("On the day");
    expect(leadLabel(1)).toBe("1 day before");
    expect(leadLabel(2)).toBe("2 days before");
    expect(leadLabel(7)).toBe("A week before");
    expect(leadLabel(3)).toBe("3 days before");
  });

  it("labels chips with the episode code for series and the type otherwise", () => {
    expect(episodeCode(4, 2)).toBe("S4E2");
    expect(episodeCode(null, 2)).toBeNull();
    expect(eventChipLabel({ type: "EpisodeAir", season: 4, episode: 2 })).toBe("S4E2");
    expect(eventChipLabel({ type: "Theatrical", season: null, episode: null })).toBe("Theatrical");
  });
});
