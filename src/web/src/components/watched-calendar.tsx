"use client";

import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { isSameMonth, startOfMonth } from "date-fns";
import { cn } from "@/lib/utils";
import { formatDateKey, toDateKey, type CalendarMode } from "@/lib/calendar";
import { mediaServer, type WatchHistoryCalendarEvent } from "@/lib/media-server";
import {
  episodeLabel,
  filterEvents,
  formatSpan,
  formatTime,
  groupSubtitle,
  groupWatchedByDay,
  monthGridInstants,
  undatedFor,
  type WatchedGroup,
  type WatchedKindFilter,
} from "@/lib/watch-history-calendar";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { ErrorState } from "@/components/states";
import { CalendarShell } from "@/components/calendar-shell";

/** How many cards fit a day cell before the rest collapse into "+N". */
const MAX_CARDS = 3;

const FILTERS: Array<{ value: WatchedKindFilter; label: string }> = [
  { value: "all", label: "All" },
  { value: "movies", label: "Movies" },
  { value: "episodes", label: "Episodes" },
];

/**
 * The Watched mode: a screening diary over the per-play history. Read-only by design — this answers
 * "what did I finish on this date", and correcting history is a separate concern.
 */
export function WatchedCalendar({
  month,
  onModeChange,
  onMonthChange,
}: {
  month: Date;
  onModeChange: (mode: CalendarMode) => void;
  onMonthChange: (month: Date) => void;
}) {
  const [filter, setFilter] = useState<WatchedKindFilter>("all");
  const [dayDetail, setDayDetail] = useState<string | null>(null);
  const [undatedOpen, setUndatedOpen] = useState(false);

  const range = monthGridInstants(month);
  const history = useQuery({
    queryKey: ["watch-history-calendar", range.from, range.toExclusive],
    queryFn: () => mediaServer.watchHistoryCalendar(range.from, range.toExclusive),
  });

  const byDay = useMemo(
    () => groupWatchedByDay(filterEvents(history.data?.events ?? [], filter)),
    [history.data, filter],
  );

  const undatedCount = history.data ? undatedFor(history.data.undated, filter) : 0;
  const detailGroups = dayDetail ? (byDay.get(dayDetail) ?? []) : [];
  const monthIsEmpty =
    !history.isPending && !history.isError && [...byDay.keys()].every((key) => !inMonth(key, month));
  const latest = history.data?.latestWatchedAt ?? null;

  const toolbar = (
    <div className="flex items-center gap-1.5">
      <div className="bg-secondary/60 flex items-center gap-0.5 rounded-md p-0.5">
        {FILTERS.map((option) => (
          <button
            key={option.value}
            type="button"
            aria-pressed={filter === option.value}
            className={cn(
              "rounded px-2 py-0.5 text-xs font-medium transition-colors",
              filter === option.value ? "bg-background shadow-sm" : "text-muted-foreground hover:text-foreground",
            )}
            onClick={() => setFilter(option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
      {undatedCount > 0 && (
        <Button variant="outline" size="sm" onClick={() => setUndatedOpen(true)}>
          Undated {undatedCount}
        </Button>
      )}
    </div>
  );

  const renderDay = (day: Date) => {
    const groups = byDay.get(toDateKey(day)) ?? [];
    const visible = groups.length > MAX_CARDS ? groups.slice(0, MAX_CARDS - 1) : groups;
    const overflow = groups.length - visible.length;

    return (
      <>
        {history.isPending && isSameMonth(day, month) && <Skeleton className="h-5 w-full" />}
        {visible.map((group) => (
          <WatchedCard key={group.key} group={group} onClick={() => setDayDetail(toDateKey(day))} />
        ))}
        {overflow > 0 && (
          <button
            type="button"
            className="text-muted-foreground hover:text-foreground rounded px-1 text-left text-[11px] font-medium"
            onClick={() => setDayDetail(toDateKey(day))}
          >
            +{overflow} more
          </button>
        )}
      </>
    );
  };

  const overlays = (
    <>
      {history.isError && <ErrorState onRetry={() => void history.refetch()} />}

      {monthIsEmpty && (
        <p className="text-muted-foreground flex flex-wrap items-center gap-2 text-sm">
          Nothing watched this month.
          {latest && !isSameMonth(new Date(latest), month) && (
            <Button variant="link" size="sm" className="h-auto p-0" onClick={() => onMonthChange(startOfMonth(new Date(latest)))}>
              Jump to last watched month
            </Button>
          )}
        </p>
      )}

      <DayDetailDialog
        dayKey={dayDetail}
        groups={detailGroups}
        onClose={() => setDayDetail(null)}
      />

      <UndatedDialog
        open={undatedOpen}
        count={undatedCount}
        onClose={() => setUndatedOpen(false)}
      />
    </>
  );

  return (
    <CalendarShell
      mode="watched"
      month={month}
      onModeChange={onModeChange}
      onMonthChange={onMonthChange}
      toolbar={toolbar}
      renderDay={renderDay}
    >
      {overlays}
    </CalendarShell>
  );
}

/** A compact grid card: one movie (with its rewatch tally) or one series' whole day. */
function WatchedCard({ group, onClick }: { group: WatchedGroup; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={`${group.title} — ${groupSubtitle(group)} (${formatSpan(group)})`}
      className="bg-secondary/60 hover:bg-secondary flex w-full items-center gap-1 overflow-hidden rounded px-1 py-0.5 text-left"
    >
      <span className="bg-background h-6 w-4 shrink-0 overflow-hidden rounded-[3px]">
        {group.posterUrl && (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={group.posterUrl} alt="" className="h-full w-full object-cover" />
        )}
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[11px] leading-tight font-medium">{group.title}</span>
        <span className="text-muted-foreground block truncate font-mono text-[10px] leading-tight">
          {groupSubtitle(group)}
        </span>
      </span>
    </button>
  );
}

/**
 * The full day, chronologically. This is where grouping unwinds: every individual play, its exact
 * local time, and — only here — where an imported play came from.
 */
function DayDetailDialog({
  dayKey,
  groups,
  onClose,
}: {
  dayKey: string | null;
  groups: WatchedGroup[];
  onClose: () => void;
}) {
  const plays = groups.flatMap((group) => group.plays).sort((a, b) => a.watchedAt.localeCompare(b.watchedAt));

  return (
    <Dialog open={dayKey !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{dayKey ? formatDateKey(dayKey) : ""}</DialogTitle>
          <DialogDescription>Watched on this day.</DialogDescription>
        </DialogHeader>
        <div className="flex max-h-80 flex-col gap-1 overflow-y-auto">
          {plays.map((event) => (
            <PlayRow key={event.entryId} event={event} />
          ))}
        </div>
      </DialogContent>
    </Dialog>
  );
}

function PlayRow({ event }: { event: WatchHistoryCalendarEvent }) {
  const code = episodeLabel(event);
  const heading = event.kind === "Episode" ? (event.seriesTitle ?? event.title) : event.title;
  const secondary = event.kind === "Episode" ? [code, event.title].filter(Boolean).join(" · ") : null;

  return (
    <div className="flex items-center gap-3 rounded-md p-1.5">
      <div className="bg-secondary h-14 w-10 shrink-0 overflow-hidden rounded">
        {event.posterUrl && (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={event.posterUrl} alt="" className="h-full w-full object-cover" />
        )}
      </div>
      <div className="min-w-0 flex-1">
        <p className="truncate font-serif text-sm font-medium">{heading}</p>
        {secondary && <p className="text-muted-foreground truncate text-xs">{secondary}</p>}
        {event.origin === "ProviderSync" && (
          <p className="text-muted-foreground text-[11px]">Imported</p>
        )}
      </div>
      <span className="flex shrink-0 items-center gap-1.5">
        {/* The screening-log notch: the one place the brand hue appears in this view. */}
        <span className="bg-brand h-3 w-0.5 rounded-full" aria-hidden />
        <span className="font-mono text-xs tabular-nums">{formatTime(event.watchedAt)}</span>
      </span>
    </div>
  );
}

/**
 * Timeless marks live here rather than on a guessed date — a manual or pre-migration mark says the
 * item was watched, never when.
 */
function UndatedDialog({ open, count, onClose }: { open: boolean; count: number; onClose: () => void }) {
  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Watched without a date</DialogTitle>
          <DialogDescription>
            {count} {count === 1 ? "mark" : "marks"} record that something was watched, but not when —
            a manual mark, or history from before per-play tracking. They are never placed on a guessed
            day.
          </DialogDescription>
        </DialogHeader>
      </DialogContent>
    </Dialog>
  );
}

/** True when a "yyyy-MM-dd" key falls inside the displayed month (not an adjacent-month cell). */
function inMonth(dayKey: string, month: Date): boolean {
  const [year, monthNumber] = dayKey.split("-").map(Number);
  return year === month.getFullYear() && monthNumber - 1 === month.getMonth();
}
