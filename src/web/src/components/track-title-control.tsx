"use client";

import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { BellRing, CalendarPlus } from "lucide-react";
import { cn } from "@/lib/utils";
import { watchlistApi, type TrackedKind } from "@/lib/watchlist";
import { Button } from "@/components/ui/button";
import { ReminderDialog } from "@/components/reminder-dialog";

/**
 * The "Track / remind me" control in title previews: opens the shared reminder dialog for the
 * catalog title (with calendar-only tracking as the secondary action). Lights up when the title is
 * already on the user's calendar.
 */
export function TrackTitleControl({
  tmdbId,
  kind,
  title,
  year,
  posterUrl,
}: {
  tmdbId: string;
  kind: TrackedKind;
  title: string;
  year?: number | null;
  posterUrl?: string | null;
}) {
  const [open, setOpen] = useState(false);

  const tracked = useTrackedTitle(tmdbId, kind);
  const hasReminder = tracked?.reminders.some((reminder) => reminder.active) ?? false;

  return (
    <>
      <Button
        variant="outline"
        onClick={() => setOpen(true)}
        className={cn(tracked && "border-brand text-brand")}
      >
        {hasReminder ? <BellRing className="size-4" aria-hidden /> : <CalendarPlus className="size-4" aria-hidden />}
        {tracked ? "Tracked" : "Track / remind me"}
      </Button>
      <ReminderDialog
        target={{
          trackedTitleId: tracked?.trackedTitleId,
          providerRef: { provider: "tmdb", id: tmdbId },
          kind,
          title,
          year,
          posterUrl,
        }}
        showTrackOnly={!tracked}
        open={open}
        onOpenChange={setOpen}
      />
    </>
  );
}

export function useTrackedTitle(tmdbId: string | null, kind: string) {
  const watchlist = useQuery({ queryKey: ["watchlist"], queryFn: watchlistApi.list, enabled: !!tmdbId && (kind === "Movie" || kind === "Series") });
  // The kind is part of the identity: TMDb's movie and tv id spaces overlap, so matching on the id alone
  // would show a tracked series as this movie's state — and hand its trackedTitleId to a movie reminder.
  return useMemo(
    () => watchlist.data?.find((item) => item.provider === "tmdb" && item.providerId === tmdbId && item.kind === kind),
    [watchlist.data, tmdbId, kind],
  );
}
