"use client";

import type { ReactNode } from "react";
import { Clock, Loader2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Progress } from "@/components/ui/progress";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

// Every row on the Activity page — a download, a catalog move, a conversion — is the same card: a bordered
// box holding a title/actions head, an optional progress block, and a monospace stat line. The pieces live
// here so the three card types can't drift apart in border, spacing, or type scale.

/** The card shell: one bordered box per activity, blocks stacked with a shared rhythm. */
export function ActivityCard({ children }: { children: ReactNode }) {
  return <div className="flex flex-col gap-3 rounded-md border p-3">{children}</div>;
}

/** The card head: title column (title, inline markers, meta, hint) on the left, icon actions on the right. */
export function ActivityCardHeader({
  title,
  titleAttr,
  markers,
  meta,
  note,
  actions,
}: {
  title: ReactNode;
  /** Native tooltip for the truncated title, when it's plain text. */
  titleAttr?: string;
  /** Badges/glyphs inline after the title (pinned, Seeding, warnings). */
  markers?: ReactNode;
  /** Muted secondary line under the title (catalog, age, file count…). */
  meta?: ReactNode;
  /** Extra muted line under the meta, for state hints. */
  note?: ReactNode;
  /** Icon action row, right-aligned. */
  actions?: ReactNode;
}) {
  return (
    <div className="flex items-start justify-between gap-2">
      <div className="flex min-w-0 flex-col gap-1">
        <div className="flex min-w-0 items-center gap-1.5">
          <p className="truncate font-medium" title={titleAttr}>
            {title}
          </p>
          {markers}
        </div>
        {meta && <p className="text-muted-foreground text-xs">{meta}</p>}
        {note && <p className="text-muted-foreground text-xs">{note}</p>}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-1">{actions}</div>}
    </div>
  );
}

/** Progress bar plus whatever stat lines belong under it, kept tighter than the card's own block spacing. */
export function ActivityProgress({ value, children }: { value: number; children?: ReactNode }) {
  return (
    <div className="flex flex-col gap-2">
      <Progress value={Math.min(Math.max(value, 0), 100)} />
      {children}
    </div>
  );
}

/** Waiting behind the work that runs now — no bar or stats to show yet, so just say it's queued. */
export function ActivityQueued() {
  return (
    <span className="flex items-center gap-1.5">
      <Clock className="size-3.5 shrink-0" aria-hidden />
      Queued
    </span>
  );
}

/** A stat line under a progress bar: percent, rates, ETA — monospace and tabular so the digits don't jitter. */
export function ActivityStats({
  tone = "default",
  className,
  children,
}: {
  tone?: "default" | "destructive" | "muted";
  className?: string;
  children: ReactNode;
}) {
  return (
    <div
      className={cn(
        "flex flex-wrap gap-x-4 gap-y-1 font-mono text-xs tabular-nums",
        tone === "destructive" ? "text-destructive" : tone === "muted" ? "text-muted-foreground/80" : "text-muted-foreground",
        className,
      )}
    >
      {children}
    </div>
  );
}

/** One icon button in a card's action row: ghost, tooltip-labelled, with a pending spinner. */
export function IconAction({
  label,
  icon,
  onClick,
  disabled,
  pending,
  destructive,
}: {
  label: string;
  icon: ReactNode;
  onClick: () => void;
  disabled?: boolean;
  // Swaps the icon for a spinner and disables the button — feedback for commands that take a moment to
  // land (and a guard against impatient double-clicks).
  pending?: boolean;
  destructive?: boolean;
}) {
  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={label}
            aria-busy={pending}
            onClick={onClick}
            disabled={disabled || pending}
            className={cn(destructive && "text-destructive hover:text-destructive hover:bg-destructive/10")}
          >
            {pending ? <Loader2 className="animate-spin" /> : icon}
          </Button>
        }
      />
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}
