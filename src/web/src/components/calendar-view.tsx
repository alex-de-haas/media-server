"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { addMonths, format, isSameMonth, isToday, startOfMonth } from "date-fns";
import { Bell, CalendarPlus, Check, ChevronLeft, ChevronRight, ListVideo } from "lucide-react";
import { cn } from "@/lib/utils";
import {
  eventChipLabel,
  formatDateKey,
  groupEventsByDay,
  monthGridDays,
  monthGridRange,
  toDateKey,
  WEEKDAY_LABELS,
} from "@/lib/calendar";
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

/**
 * The /calendar page: a Monday-first month grid of the user's tracked release dates, with a toolbar for
 * month navigation, adding a title (TMDb search), and the Tracked / Reminders slide-overs. Clicking a
 * chip opens the shared reminder dialog with the type prefilled — the same (title, type) reminder as
 * every other entry point.
 */
export function CalendarView() {
  const [month, setMonth] = useState(() => startOfMonth(new Date()));
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
  const days = monthGridDays(month);
  const detailEvents = dayDetail ? (byDay.get(dayDetail) ?? []) : [];

  return (
    <section className="flex flex-col gap-4">
      <header className="flex flex-wrap items-center gap-2">
        <h1 className="text-2xl font-semibold tracking-tight">Calendar</h1>
        <div className="ml-auto flex items-center gap-1">
          <Button variant="ghost" size="icon-sm" aria-label="Previous month" onClick={() => setMonth((current) => addMonths(current, -1))}>
            <ChevronLeft />
          </Button>
          <span className="w-36 text-center text-sm font-medium tabular-nums">{format(month, "MMMM yyyy")}</span>
          <Button variant="ghost" size="icon-sm" aria-label="Next month" onClick={() => setMonth((current) => addMonths(current, 1))}>
            <ChevronRight />
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setMonth(startOfMonth(new Date()))}>
            Today
          </Button>
        </div>
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
      </header>

      {events.isError ? (
        <ErrorState onRetry={() => void events.refetch()} />
      ) : (
        <div className="overflow-hidden rounded-lg border">
          <div className="grid grid-cols-7 border-b">
            {WEEKDAY_LABELS.map((label) => (
              <div key={label} className="text-muted-foreground px-2 py-1.5 text-center text-xs font-medium">
                {label}
              </div>
            ))}
          </div>
          <div className="grid grid-cols-7">
            {days.map((day, index) => (
              <DayCell
                key={toDateKey(day)}
                day={day}
                month={month}
                events={byDay.get(toDateKey(day)) ?? []}
                pending={events.isPending}
                topRow={index < 7}
                onEventClick={openReminderFor}
                onOverflow={() => setDayDetail(toDateKey(day))}
              />
            ))}
          </div>
        </div>
      )}

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
    </section>
  );
}

/** How many chips fit a day cell before the rest collapse into "+N". */
const MAX_CHIPS = 3;

function DayCell({
  day,
  month,
  events,
  pending,
  topRow,
  onEventClick,
  onOverflow,
}: {
  day: Date;
  month: Date;
  events: CalendarEvent[];
  pending: boolean;
  topRow: boolean;
  onEventClick: (event: CalendarEvent) => void;
  onOverflow: () => void;
}) {
  const outside = !isSameMonth(day, month);
  const today = isToday(day);
  const visible = events.length > MAX_CHIPS ? events.slice(0, MAX_CHIPS - 1) : events;
  const overflow = events.length - visible.length;

  return (
    <div className={cn("flex min-h-24 flex-col gap-1 border-r p-1 last-of-type:border-r-0 [&:nth-of-type(7n)]:border-r-0", !topRow && "border-t", outside && "bg-secondary/30")}>
      <span
        className={cn(
          "flex size-6 items-center justify-center self-end rounded-full text-xs tabular-nums",
          outside && "text-muted-foreground/60",
          today && "bg-brand text-white font-semibold",
        )}
      >
        {day.getDate()}
      </span>
      {pending && !outside && <Skeleton className="h-5 w-full" />}
      {visible.map((event) => (
        <EventChip key={event.releaseId} event={event} onClick={() => onEventClick(event)} />
      ))}
      {overflow > 0 && (
        <button
          type="button"
          className="text-muted-foreground hover:text-foreground rounded px-1 text-left text-[11px] font-medium"
          onClick={onOverflow}
        >
          +{overflow} more
        </button>
      )}
    </div>
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
