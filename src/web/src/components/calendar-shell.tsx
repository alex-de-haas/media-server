"use client";

import type { ReactNode } from "react";
import { addMonths, format, isSameMonth, isToday, startOfMonth } from "date-fns";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { cn } from "@/lib/utils";
import { monthGridDays, toDateKey, WEEKDAY_LABELS, type CalendarMode } from "@/lib/calendar";
import { Button } from "@/components/ui/button";

/**
 * Everything both calendar modes share: the heading, the mode switch, month navigation, and the
 * Monday-first grid frame. Each mode supplies its own toolbar and day contents, so neither has to
 * reimplement navigation — and neither accumulates `if (mode === …)` branches inside the other.
 */
export function CalendarShell({
  mode,
  month,
  onModeChange,
  onMonthChange,
  toolbar,
  renderDay,
  children,
}: {
  mode: CalendarMode;
  month: Date;
  onModeChange: (mode: CalendarMode) => void;
  onMonthChange: (month: Date) => void;
  /** Mode-specific actions, shown beside the mode switch. */
  toolbar?: ReactNode;
  /** Renders one day cell's contents; the frame, date badge, and outside/today styling stay here. */
  renderDay: (day: Date) => ReactNode;
  /** Rendered under the grid — dialogs, drawers, and any out-of-grid lists. */
  children?: ReactNode;
}) {
  const days = monthGridDays(month);

  return (
    <section className="flex flex-col gap-4">
      <header className="flex flex-wrap items-center gap-2">
        <h1 className="text-2xl font-semibold tracking-tight">Calendar</h1>
        <ModeSwitch mode={mode} onChange={onModeChange} />
        <div className="ml-auto flex items-center gap-1">
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label="Previous month"
            onClick={() => onMonthChange(addMonths(month, -1))}
          >
            <ChevronLeft />
          </Button>
          <span className="w-36 text-center text-sm font-medium tabular-nums">
            {format(month, "MMMM yyyy")}
          </span>
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label="Next month"
            onClick={() => onMonthChange(addMonths(month, 1))}
          >
            <ChevronRight />
          </Button>
          <Button variant="ghost" size="sm" onClick={() => onMonthChange(startOfMonth(new Date()))}>
            Today
          </Button>
        </div>
        {toolbar}
      </header>

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
            <DayFrame key={toDateKey(day)} day={day} month={month} topRow={index < 7}>
              {renderDay(day)}
            </DayFrame>
          ))}
        </div>
      </div>

      {children}
    </section>
  );
}

function ModeSwitch({ mode, onChange }: { mode: CalendarMode; onChange: (mode: CalendarMode) => void }) {
  return (
    <div className="bg-secondary/60 flex items-center gap-0.5 rounded-md p-0.5" role="tablist" aria-label="Calendar mode">
      {(["releases", "watched"] as const).map((value) => (
        <button
          key={value}
          type="button"
          role="tab"
          aria-selected={mode === value}
          className={cn(
            "rounded px-2.5 py-1 text-sm font-medium capitalize transition-colors",
            mode === value ? "bg-background shadow-sm" : "text-muted-foreground hover:text-foreground",
          )}
          onClick={() => onChange(value)}
        >
          {value}
        </button>
      ))}
    </div>
  );
}

/** The cell chrome: borders, the outside-month wash, and the date badge. */
function DayFrame({
  day,
  month,
  topRow,
  children,
}: {
  day: Date;
  month: Date;
  topRow: boolean;
  children: ReactNode;
}) {
  const outside = !isSameMonth(day, month);

  return (
    <div
      className={cn(
        "flex min-h-24 flex-col gap-1 border-r p-1 last-of-type:border-r-0 [&:nth-of-type(7n)]:border-r-0",
        !topRow && "border-t",
        outside && "bg-secondary/30",
      )}
    >
      <span
        className={cn(
          "flex size-6 items-center justify-center self-end rounded-full text-xs tabular-nums",
          outside && "text-muted-foreground/60",
          isToday(day) && "bg-brand font-semibold text-white",
        )}
      >
        {day.getDate()}
      </span>
      {children}
    </div>
  );
}
