"use client";

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { isSameMonth, startOfMonth } from "date-fns";
import { Trash2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { toast } from "@/lib/toast";
import { errorMessage } from "@/lib/ui";
import { formatDateKey, toDateKey, type CalendarMode } from "@/lib/calendar";
import {
  mediaServer,
  type WatchHistoryCalendarEvent,
  type WatchHistoryUndatedEntry,
} from "@/lib/media-server";
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
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
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
 * Everything a deleted play can move: the diary itself, and the per-item watched state and play count
 * projected from it. Invalidating an inactive query only marks it stale, so listing the library keys
 * costs nothing here — it stops a stale watched badge appearing on the way back.
 */
const AFFECTED_BY_DELETE = [
  ["watch-history-calendar"],
  ["watch-history-undated"],
  ["library"],
  ["library-detail"],
  ["episodes"],
  ["nextup"],
  ["resume"],
  ["recent"],
  ["removed-titles"],
] as const;

/** The one play a confirmation is currently asking about. */
type DeleteTarget = { entryId: string; heading: string; detail: string | null };

/**
 * The Watched mode: a screening diary over the per-play history. It answers "what did I finish on
 * this date"; the only edit it offers is deleting a single play that should not be there, from the
 * day detail or the undated list.
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
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null);
  const queryClient = useQueryClient();

  const range = monthGridInstants(month);
  const history = useQuery({
    queryKey: ["watch-history-calendar", range.from, range.toExclusive],
    queryFn: () => mediaServer.watchHistoryCalendar(range.from, range.toExclusive),
  });

  const remove = useMutation({
    mutationFn: (entryId: string) => mediaServer.deleteWatchHistoryEntry(entryId),
    onSuccess: () => {
      setDeleteTarget(null);
      for (const queryKey of AFFECTED_BY_DELETE) {
        void queryClient.invalidateQueries({ queryKey });
      }
      toast.success("Play deleted");
    },
    // The confirmation stays open on failure: closing it would leave the row still there with only a
    // toast to explain why, which reads as the delete having silently done nothing.
    onError: (error) => toast.error("Couldn’t delete this play", { description: errorMessage(error) }),
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

  // Only days inside the displayed month, chronologically — adjacent-month grid cells make sense in
  // a grid but not in a list.
  const agendaDays = [...byDay.entries()]
    .filter(([key]) => inMonth(key, month))
    .sort(([a], [b]) => a.localeCompare(b));

  const agenda = (
    <div className="flex flex-col gap-4">
      {history.isPending && <Skeleton className="h-16 w-full" />}
      {agendaDays.map(([dayKey, groups]) => (
        <div key={dayKey} className="flex flex-col gap-1">
          <div className="flex items-baseline justify-between gap-2">
            <h2 className="text-sm font-medium">{formatDateKey(dayKey)}</h2>
            <span className="text-muted-foreground font-mono text-xs">
              {playCountLabel(groups)}
            </span>
          </div>
          {groups.map((group) => (
            <button
              key={group.key}
              type="button"
              onClick={() => setDayDetail(dayKey)}
              className="hover:bg-secondary/60 flex items-center gap-3 rounded-md p-1.5 text-left"
            >
              <span className="bg-secondary h-14 w-10 shrink-0 overflow-hidden rounded">
                {group.posterUrl && (
                  // eslint-disable-next-line @next/next/no-img-element
                  <img src={group.posterUrl} alt="" className="h-full w-full object-cover" />
                )}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block truncate text-sm font-medium">{group.title}</span>
                <span className="text-muted-foreground block truncate text-xs">{groupSubtitle(group)}</span>
              </span>
              <span className="text-muted-foreground shrink-0 font-mono text-xs tabular-nums">
                {formatSpan(group)}
              </span>
            </button>
          ))}
        </div>
      ))}
    </div>
  );

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
        onDelete={setDeleteTarget}
      />

      <UndatedDialog
        open={undatedOpen}
        filter={filter}
        onClose={() => setUndatedOpen(false)}
        onDelete={setDeleteTarget}
      />

      <AlertDialog open={deleteTarget !== null} onOpenChange={(open) => !open && setDeleteTarget(null)}>
        <AlertDialogContent className="sm:max-w-md">
          <AlertDialogHeader>
            <AlertDialogTitle>Delete this play?</AlertDialogTitle>
            <AlertDialogDescription>
              Removes one recorded play of{" "}
              <span className="text-foreground font-medium">{deleteTarget?.heading}</span>
              {deleteTarget?.detail && <> ({deleteTarget.detail})</>} from your history. The play
              count follows, and a connected service is asked to drop the entry when this app is the
              one that put it there. This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel size="sm">Cancel</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              size="sm"
              disabled={remove.isPending}
              onClick={() => deleteTarget && remove.mutate(deleteTarget.entryId)}
            >
              {remove.isPending ? "Deleting…" : "Delete"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
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
      agenda={agenda}
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
  onDelete,
}: {
  dayKey: string | null;
  groups: WatchedGroup[];
  onClose: () => void;
  onDelete: (target: DeleteTarget) => void;
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
          {/* Reachable by deleting the day's last play: the dialog stays put rather than vanishing
              under the user mid-interaction. */}
          {plays.length === 0 && (
            <p className="text-muted-foreground px-1.5 text-sm">Nothing is left on this day.</p>
          )}
          {plays.map((event) => (
            <PlayRow key={event.entryId} event={event} onDelete={onDelete} />
          ))}
        </div>
      </DialogContent>
    </Dialog>
  );
}

function PlayRow({
  event,
  onDelete,
}: {
  event: WatchHistoryCalendarEvent;
  onDelete: (target: DeleteTarget) => void;
}) {
  const code = episodeLabel(event);
  const heading = event.kind === "Episode" ? (event.seriesTitle ?? event.title) : event.title;
  const secondary = event.kind === "Episode" ? [code, event.title].filter(Boolean).join(" · ") : null;

  return (
    <div className="hover:bg-secondary/60 flex items-center gap-3 rounded-md p-1.5">
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
      <DeleteEntryButton
        // Named down to the timestamp: a day can hold two plays of one movie, and two controls with
        // the same accessible name leave a screen reader unable to say which one it is on.
        label={`Delete this play: ${[heading, secondary, formatTime(event.watchedAt)].filter(Boolean).join(" · ")}`}
        onClick={() =>
          onDelete({
            entryId: event.entryId,
            heading,
            detail: [secondary, formatTime(event.watchedAt)].filter(Boolean).join(" · "),
          })
        }
      />
    </div>
  );
}

/**
 * The one edit this view offers. Always rendered rather than revealed on hover: a touch device has no
 * hover, and a control that only exists on a pointer would be unreachable on the phone agenda.
 */
function DeleteEntryButton({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <Button
      variant="ghost"
      size="icon"
      aria-label={label}
      title={label}
      className="text-muted-foreground hover:text-destructive size-7 shrink-0"
      onClick={onClick}
    >
      <Trash2 className="size-3.5" />
    </Button>
  );
}

/**
 * Timeless marks live here rather than on a guessed date — a manual or pre-migration mark says the
 * item was watched, never when. Fetched only when opened: most sessions never ask.
 */
function UndatedDialog({
  open,
  filter,
  onClose,
  onDelete,
}: {
  open: boolean;
  filter: WatchedKindFilter;
  onClose: () => void;
  onDelete: (target: DeleteTarget) => void;
}) {
  const kind = filter === "movies" ? "Movie" : filter === "episodes" ? "Episode" : undefined;
  const undated = useQuery({
    queryKey: ["watch-history-undated", kind ?? "all"],
    queryFn: () => mediaServer.watchHistoryUndated(kind),
    enabled: open,
  });

  const entries = undated.data?.entries ?? [];
  const total = undated.data?.total ?? 0;
  const truncated = total > entries.length;

  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Watched without a date</DialogTitle>
          <DialogDescription>
            These record that something was watched, but not when — a manual mark, or history from
            before per-play tracking. They are never placed on a guessed day.
          </DialogDescription>
        </DialogHeader>
        <div className="flex max-h-80 flex-col gap-1 overflow-y-auto">
          {undated.isPending && <Skeleton className="h-14 w-full" />}
          {/* Reachable by deleting the last mark: the `Undated N` control that opened this is already
              gone, so the dialog has to say what happened rather than sit empty. */}
          {undated.isSuccess && entries.length === 0 && (
            <p className="text-muted-foreground px-1.5 text-sm">Nothing is left without a date.</p>
          )}
          {truncated && (
            <p className="text-muted-foreground px-1.5 text-xs">
              Showing the most recent {entries.length} of {total}.
            </p>
          )}
          {entries.map((entry) => (
            <UndatedRow key={entry.entryId} entry={entry} onDelete={onDelete} />
          ))}
        </div>
      </DialogContent>
    </Dialog>
  );
}

function UndatedRow({
  entry,
  onDelete,
}: {
  entry: WatchHistoryUndatedEntry;
  onDelete: (target: DeleteTarget) => void;
}) {
  const heading = entry.kind === "Episode" ? (entry.seriesTitle ?? entry.title) : entry.title;
  const secondary =
    entry.kind === "Episode"
      ? [
          entry.seasonNumber != null && entry.episodeNumber != null
            ? `S${entry.seasonNumber}E${entry.episodeNumber}`
            : null,
          entry.title,
        ]
          .filter(Boolean)
          .join(" · ")
      : null;

  return (
    <div className="hover:bg-secondary/60 flex items-center gap-3 rounded-md p-1.5">
      <div className="bg-secondary h-14 w-10 shrink-0 overflow-hidden rounded">
        {entry.posterUrl && (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={entry.posterUrl} alt="" className="h-full w-full object-cover" />
        )}
      </div>
      <div className="min-w-0 flex-1">
        <p className="truncate font-serif text-sm font-medium">{heading}</p>
        {secondary && <p className="text-muted-foreground truncate text-xs">{secondary}</p>}
      </div>
      <DeleteEntryButton
        label={`Delete this undated mark: ${[heading, secondary].filter(Boolean).join(" · ")}`}
        onClick={() => onDelete({ entryId: entry.entryId, heading, detail: secondary })}
      />
    </div>
  );
}

/** "3 plays" — the day's real play count, not its collapsed card count. */
function playCountLabel(groups: WatchedGroup[]): string {
  const plays = groups.reduce((total, group) => total + group.plays.length, 0);
  return plays === 1 ? "1 play" : `${plays} plays`;
}

/** True when a "yyyy-MM-dd" key falls inside the displayed month (not an adjacent-month cell). */
function inMonth(dayKey: string, month: Date): boolean {
  const [year, monthNumber] = dayKey.split("-").map(Number);
  return year === month.getFullYear() && monthNumber - 1 === month.getMonth();
}
