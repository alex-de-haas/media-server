"use client";

import { useRouter } from "next/navigation";
import { calendarHref, parseCalendarMode, parseMonthParam, type CalendarMode } from "@/lib/calendar";
import { ReleasesCalendar } from "@/components/releases-calendar";
import { WatchedCalendar } from "@/components/watched-calendar";

/**
 * The /calendar page. Mode and month live in the URL rather than in component state, so a view is
 * shareable and the back button works; only the selected mode mounts, so the other never queries.
 *
 * The params arrive as props from the server component — not from `useSearchParams` — because that
 * hook returns nothing until hydration on a static route, and navigating during that window would
 * silently use the current month instead of the one in the link.
 */
export function CalendarView({ view, month: monthParam }: { view: string | null; month: string | null }) {
  const router = useRouter();
  const mode = parseCalendarMode(view);
  const month = parseMonthParam(monthParam);

  const navigate = (nextMode: CalendarMode, nextMonth: Date) =>
    router.replace(calendarHref(nextMode, nextMonth), { scroll: false });

  const onModeChange = (next: CalendarMode) => navigate(next, month);
  const onMonthChange = (next: Date) => navigate(mode, next);

  return mode === "watched" ? (
    <WatchedCalendar month={month} onModeChange={onModeChange} onMonthChange={onMonthChange} />
  ) : (
    <ReleasesCalendar month={month} onModeChange={onModeChange} onMonthChange={onMonthChange} />
  );
}
