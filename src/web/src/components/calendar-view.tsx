"use client";

import { useRouter } from "next/navigation";
import { calendarHref, parseCalendarMode, parseMonthParam, type CalendarMode } from "@/lib/calendar";
import { ReleasesCalendar } from "@/components/releases-calendar";
import { WatchedCalendar } from "@/components/watched-calendar";

/**
 * The /calendar page. Mode and month live in the URL rather than in component state, so a view is
 * shareable; only the selected mode mounts, so the other never queries.
 *
 * Switching mode pushes — it is a destination a user may want to come back from. Paging months
 * replaces: stepping through a year should not bury the previous page under twelve history entries.
 *
 * The params arrive as props from the server component — not from `useSearchParams` — because that
 * hook returns nothing until hydration on a static route, and navigating during that window would
 * silently use the current month instead of the one in the link.
 */
export function CalendarView({ view, month: monthParam }: { view: string | null; month: string | null }) {
  const router = useRouter();
  const mode = parseCalendarMode(view);
  const month = parseMonthParam(monthParam);

  const onModeChange = (next: CalendarMode) =>
    router.push(calendarHref(next, month), { scroll: false });
  const onMonthChange = (next: Date) =>
    router.replace(calendarHref(mode, next), { scroll: false });

  return mode === "watched" ? (
    <WatchedCalendar month={month} onModeChange={onModeChange} onMonthChange={onMonthChange} />
  ) : (
    <ReleasesCalendar month={month} onModeChange={onModeChange} onMonthChange={onMonthChange} />
  );
}
