"use client";

import { useId, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { BellPlus, CalendarPlus } from "lucide-react";
import { toast } from "@/lib/toast";
import { errorMessage } from "@/lib/ui";
import { formatDateKey, LEAD_OPTIONS, releaseTypeLabel } from "@/lib/calendar";
import {
  watchlistApi,
  type ReleaseType,
  type ReminderResolution,
  type TrackedKind,
} from "@/lib/watchlist";
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
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

/** The title a reminder targets: by tracked-title id (calendar/drawer) or provider ref (detail/search). */
export interface ReminderTarget {
  trackedTitleId?: string;
  providerRef?: { provider: string; id: string };
  kind: TrackedKind;
  title: string;
  year?: number | null;
  posterUrl?: string | null;
}

/** Per-kind valid reminder types; Theatrical + Digital are foregrounded, Premiere is secondary. */
function typeChoices(kind: TrackedKind): ReleaseType[] {
  return kind === "Series" ? ["EpisodeAir"] : ["Theatrical", "Digital", "Premiere"];
}

function resolutionToast(resolution: ReminderResolution) {
  const detail = resolution.detail ?? undefined;
  switch (resolution.state) {
    case "scheduled":
      toast.success(`Reminder set — ${resolution.date ? formatDateKey(resolution.date) : "scheduled"}`, {
        description: detail,
      });
      break;
    case "alreadyReleased":
      toast.info(`Already released${resolution.date ? ` on ${formatDateKey(resolution.date)}` : ""}`, {
        description: detail ?? "You'll still get a one-time notification.",
      });
      break;
    default:
      toast.info("No date announced yet", {
        description: detail ?? "The reminder is pending — it schedules itself once a date is known.",
      });
  }
}

/**
 * The one shared reminder dialog — "remind me about the {type} release of {title}, {lead} before, at
 * {time}" — opened from a calendar chip, a drawer row, or a Movie/Series detail page. The same dialog
 * covers a known date, a not-yet-announced date (resolves to pending), and one already passed. Creating a
 * reminder also tracks the title (remind implies track); `showTrackOnly` additionally offers calendar-only
 * tracking without a reminder.
 */
export function ReminderDialog({
  target,
  defaultType,
  showTrackOnly = false,
  open,
  onOpenChange,
}: {
  target: ReminderTarget;
  defaultType?: ReleaseType;
  showTrackOnly?: boolean;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const typeId = useId();
  const leadId = useId();
  const timeId = useId();
  const queryClient = useQueryClient();

  const choices = typeChoices(target.kind);
  const initialType = defaultType && choices.includes(defaultType) ? defaultType : choices[0];
  const [releaseType, setReleaseType] = useState<ReleaseType>(initialType);
  const [leadDays, setLeadDays] = useState(0);
  const [notifyAt, setNotifyAt] = useState("09:00");

  // Reset per open so a prior configuration doesn't linger across titles.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setReleaseType(initialType);
      setLeadDays(0);
      setNotifyAt("09:00");
    }
  }

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: ["watchlist"] });
    void queryClient.invalidateQueries({ queryKey: ["watchlist-calendar"] });
    void queryClient.invalidateQueries({ queryKey: ["reminders"] });
  };

  const remind = useMutation({
    mutationFn: () =>
      watchlistApi.createReminder({
        trackedTitleId: target.trackedTitleId ?? null,
        providerRef: target.trackedTitleId ? null : (target.providerRef ?? null),
        kind: target.kind,
        releaseType,
        leadDays,
        notifyAt: notifyAt || "09:00",
        title: target.title,
        year: target.year ?? null,
        posterUrl: target.posterUrl ?? null,
      }),
    onSuccess: (resolution) => {
      resolutionToast(resolution);
      invalidate();
      onOpenChange(false);
    },
    onError: (error) => toast.error("Couldn’t set the reminder", { description: errorMessage(error) }),
  });

  const trackOnly = useMutation({
    mutationFn: () =>
      watchlistApi.add({
        providerRef: target.providerRef!,
        kind: target.kind,
        title: target.title,
        year: target.year ?? null,
        posterUrl: target.posterUrl ?? null,
      }),
    onSuccess: () => {
      toast.success("Added to your calendar");
      invalidate();
      onOpenChange(false);
    },
    onError: (error) => toast.error("Couldn’t track the title", { description: errorMessage(error) }),
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Track / remind me</DialogTitle>
          <DialogDescription className="truncate" title={target.title}>
            Get notified about a release of{" "}
            <span className="text-foreground font-medium">{target.title}</span>
            {target.kind === "Series" && " — reminders recur for each new episode"}.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-3 text-sm">
          <Field>
            <FieldLabel htmlFor={typeId}>Release type</FieldLabel>
            <Select value={releaseType} onValueChange={(value) => setReleaseType(value as ReleaseType)}>
              <SelectTrigger id={typeId} className="w-full" disabled={choices.length === 1}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {choices.map((choice) => (
                  <SelectItem key={choice} value={choice}>
                    {choice === "EpisodeAir" ? "Episode airs" : releaseTypeLabel(choice)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {target.kind === "Series" && (
              <p className="text-muted-foreground text-xs">
                Episode tracking turns on automatically (future episodes only) so the reminder can fire.
              </p>
            )}
          </Field>

          <div className="flex gap-3">
            <Field className="flex-1">
              <FieldLabel htmlFor={leadId}>Remind</FieldLabel>
              <Select value={String(leadDays)} onValueChange={(value) => setLeadDays(Number(value))}>
                <SelectTrigger id={leadId} className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {LEAD_OPTIONS.map((option) => (
                    <SelectItem key={option.value} value={String(option.value)}>
                      {option.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field className="w-32">
              <FieldLabel htmlFor={timeId}>At</FieldLabel>
              <Input id={timeId} type="time" value={notifyAt} onChange={(event) => setNotifyAt(event.target.value)} />
            </Field>
          </div>
        </div>

        <DialogFooter>
          {showTrackOnly && target.providerRef && (
            <Button variant="outline" onClick={() => trackOnly.mutate()} disabled={trackOnly.isPending || remind.isPending}>
              <CalendarPlus className="size-4" aria-hidden /> Track only
            </Button>
          )}
          <Button onClick={() => remind.mutate()} disabled={remind.isPending || trackOnly.isPending}>
            <BellPlus className="size-4" aria-hidden /> {remind.isPending ? "Saving…" : "Set reminder"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
