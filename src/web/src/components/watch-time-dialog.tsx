"use client";

import { useId, useState, type ReactNode } from "react";
import { Clock } from "lucide-react";
import { isFutureInstant, toLocalInputValue, toUtcInstant } from "@/lib/watch-time";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";

/**
 * "When did you watch this?" — the one field shared by logging a play by hand and by giving an undated
 * mark its time. Both answer the same question, so they ask it the same way; only the wording and what
 * happens on submit differ, and those are the caller's.
 *
 * The field is wall-clock local time and the submitted value is a UTC instant: the calendar buckets by
 * the browser's local day, so a play at 00:30 has to land on the day it was watched.
 */
export function WatchTimeDialog({
  open,
  onOpenChange,
  heading,
  description,
  confirmLabel,
  pending,
  onSubmit,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  heading: string;
  description: ReactNode;
  confirmLabel: string;
  pending: boolean;
  onSubmit: (watchedAt: string) => void;
}) {
  const fieldId = useId();
  const [value, setValue] = useState(() => toLocalInputValue(new Date()));

  // Re-stamped per open, not per mount: "now" has to mean now, and a dialog reopened an hour later
  // would otherwise offer the hour-old time it was first built with.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setValue(toLocalInputValue(new Date()));
    }
  }

  const instant = toUtcInstant(value);
  // Checked here as well as on the server, so the refusal arrives while the field is still in front of
  // the user rather than as a toast after a round trip.
  const error = !instant
    ? "Pick a date and time."
    : isFutureInstant(instant)
      ? "That is in the future."
      : null;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{heading}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>

        <Field>
          <FieldLabel htmlFor={fieldId}>Watched at</FieldLabel>
          <div className="flex gap-2">
            <Input
              id={fieldId}
              type="datetime-local"
              value={value}
              max={toLocalInputValue(new Date())}
              onChange={(event) => setValue(event.target.value)}
              aria-invalid={error != null}
              aria-describedby={error ? `${fieldId}-error` : undefined}
              className="flex-1"
            />
            <Button
              type="button"
              variant="outline"
              onClick={() => setValue(toLocalInputValue(new Date()))}
              aria-label="Set to now"
            >
              <Clock className="size-4" aria-hidden /> Now
            </Button>
          </div>
          {error && (
            <p id={`${fieldId}-error`} className="text-destructive text-xs">
              {error}
            </p>
          )}
        </Field>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={pending}>
            Cancel
          </Button>
          <Button onClick={() => instant && onSubmit(instant)} disabled={pending || error != null}>
            {pending ? "Saving…" : confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
