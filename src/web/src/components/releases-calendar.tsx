"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { isSameMonth } from "date-fns";
import { Bell, CalendarPlus, Check, ListVideo } from "lucide-react";
import {
  eventChipLabel,
  formatDateKey,
  groupEventsByDay,
  monthGridRange,
  toDateKey,
  type CalendarMode,
} from "@/lib/calendar";
import { CalendarShell } from "@/components/calendar-shell";
import { watchlistApi, type CalendarEvent, type WatchlistItem } from "@/lib/watchlist";
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
import { AddTitleDialog } from "@/components/add-title-dialog";
import { ReminderDialog, type ReminderTarget } from "@/components/reminder-dialog";
import { RemindersDrawer } from "@/components/reminders-drawer";
import { TrackedDrawer } from "@/components/tracked-drawer";

interface ReminderPrompt {
  target: ReminderTarget;
  defaultType?: CalendarEvent["type"];
}

/** How many chips fit a day cell before the rest collapse into "+N". */
const MAX_CHIPS = 3;

/**
 * The Releases mode: the user's tracked release dates, with a toolbar for adding a title (TMDb search)
 * and the Tracked / Reminders slide-overs. Clicking a chip opens the shared reminder dialog with the
 * type prefilled — the same (title, type) reminder as every other entry point.
 */
export function ReleasesCalendar({
  month,
  onModeChange,
  onMonthChange,
}: {
  month: Date;
  onModeChange: (mode: CalendarMode) => void;
  onMonthChange: (month: Date) => void;
}) {
  const [addOpen, setAddOpen] = useState(false);
  const [trackedOpen, setTrackedOpen] = useState(false);
  const [remindersOpen, setRemindersOpen] = useState(false);
  const [reminderPrompt, setReminderPrompt] = useState<ReminderPrompt | null>(null);
  const [reminderOpen, setReminderOpen] = useState(false);
  const [dayDetail, setDayDetail] = useState<string | null>(null);

  const range = monthGridRange(month);
  const events = useQuery({
    queryKey: ["watchlist-calendar", range.from, range.to],
    queryFn: () => watchlistApi.calendar(range.from, range.to),
  });

  const openReminderFor = (event: CalendarEvent) => {
    setDayDetail(null);
    setReminderPrompt({
      target: {
        trackedTitleId: event.trackedTitleId,
        kind: event.kind,
        title: event.title,
        posterUrl: event.posterUrl,
      },
      defaultType: event.type,
    });
    setReminderOpen(true);
  };

  const openReminderForItem = (item: WatchlistItem) => {
    setTrackedOpen(false);
    setReminderPrompt({
      target: {
        trackedTitleId: item.trackedTitleId,
        kind: item.kind,
        title: item.title,
        year: item.year,
        posterUrl: item.posterUrl,
      },
    });
    setReminderOpen(true);
  };

  const byDay = groupEventsByDay(events.data ?? []);
  const detailEvents = dayDetail ? (byDay.get(dayDetail) ?? []) : [];

  const toolbar = (
    <div className="flex items-center gap-1.5">
      <Button variant="secondary" size="sm" onClick={() => setAddOpen(true)}>
        <CalendarPlus className="size-4" aria-hidden /> Add title
      </Button>
      <Button variant="outline" size="icon-sm" aria-label="Tracked titles" onClick={() => setTrackedOpen(true)}>
        <ListVideo />
      </Button>
      <Button variant="outline" size="icon-sm" aria-label="Reminders" onClick={() => setRemindersOpen(true)}>
        <Bell />
      </Button>
    </div>
  );

  const renderDay = (day: Date) => {
    const dayEvents = byDay.get(toDateKey(day)) ?? [];
    const visible = dayEvents.length > MAX_CHIPS ? dayEvents.slice(0, MAX_CHIPS - 1) : dayEvents;
    const overflow = dayEvents.length - visible.length;

    return (
      <>
        {events.isPending && isSameMonth(day, month) && <Skeleton className="h-5 w-full" />}
        {visible.map((event) => (
          <EventChip key={event.releaseId} event={event} onClick={() => openReminderFor(event)} />
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
      {events.isError && <ErrorState onRetry={() => void events.refetch()} />}

      <AddTitleDialog open={addOpen} onOpenChange={setAddOpen} />
      <TrackedDrawer
        open={trackedOpen}
        onOpenChange={setTrackedOpen}
        onAddTitle={() => {
          setTrackedOpen(false);
          setAddOpen(true);
        }}
        onRemind={openReminderForItem}
      />
      <RemindersDrawer open={remindersOpen} onOpenChange={setRemindersOpen} />
      {reminderPrompt && (
        <ReminderDialog
          target={reminderPrompt.target}
          defaultType={reminderPrompt.defaultType}
          open={reminderOpen}
          onOpenChange={setReminderOpen}
        />
      )}

      {/* The "+N" overflow: the full day as larger rows, each opening the reminder dialog. */}
      <Dialog open={dayDetail !== null} onOpenChange={(open) => !open && setDayDetail(null)}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>{dayDetail ? formatDateKey(dayDetail) : ""}</DialogTitle>
            <DialogDescription>Releases on this day.</DialogDescription>
          </DialogHeader>
          <div className="flex max-h-80 flex-col gap-1 overflow-y-auto">
            {detailEvents.map((event) => (
              <button
                key={event.releaseId}
                type="button"
                className="hover:bg-secondary/60 flex items-center gap-3 rounded-md p-1.5 text-left"
                onClick={() => openReminderFor(event)}
              >
                <div className="bg-secondary h-14 w-10 shrink-0 overflow-hidden rounded">
                  {event.posterUrl && (
                    // eslint-disable-next-line @next/next/no-img-element
                    <img src={event.posterUrl} alt="" className="h-full w-full object-cover" />
                  )}
                </div>
                <div className="min-w-0 flex-1">
                  <p className="flex items-center gap-1.5 truncate text-sm font-medium">
                    <span className="truncate">{event.title}</span>
                    {event.inLibrary && <Check className="text-brand size-3.5 shrink-0" aria-label="In library" />}
                  </p>
                  <p className="text-muted-foreground text-xs">{eventChipLabel(event)}</p>
                </div>
                {event.hasReminder && <Bell className="text-brand size-4 shrink-0" aria-label="Reminder set" />}
              </button>
            ))}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );

  return (
    <CalendarShell
      mode="releases"
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

/**
 * A rich release chip: poster thumb, title, type/episode label, a bell when a reminder targets its type,
 * and an in-library tick — modeled on the tracked-title row.
 */
function EventChip({ event, onClick }: { event: CalendarEvent; onClick: () => void }) {
  const moved = event.previousDate ? ` (moved from ${formatDateKey(event.previousDate)})` : "";
  return (
    <button
      type="button"
      onClick={onClick}
      title={`${event.title} — ${eventChipLabel(event)}${moved}`}
      className="bg-secondary/60 hover:bg-secondary flex w-full items-center gap-1 overflow-hidden rounded px-1 py-0.5 text-left"
    >
      <span className="bg-background h-6 w-4 shrink-0 overflow-hidden rounded-[3px]">
        {event.posterUrl && (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={event.posterUrl} alt="" className="h-full w-full object-cover" />
        )}
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[11px] leading-tight font-medium">{event.title}</span>
        <span className="text-muted-foreground block truncate text-[10px] leading-tight">{eventChipLabel(event)}</span>
      </span>
      {event.hasReminder && <Bell className="text-brand size-3 shrink-0" aria-hidden />}
      {event.inLibrary && <Check className="text-brand size-3 shrink-0" aria-hidden />}
    </button>
  );
}
