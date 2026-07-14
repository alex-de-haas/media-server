"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Bell, CalendarPlus, Check, CircleHelp, RefreshCw, Trash2 } from "lucide-react";
import { toast } from "@/lib/toast";
import { errorMessage } from "@/lib/ui";
import { episodeCode, formatDateKey, releaseTypeLabel } from "@/lib/calendar";
import { watchlistApi, type WatchlistItem } from "@/lib/watchlist";
import { Button } from "@/components/ui/button";
import {
  Drawer,
  DrawerContent,
  DrawerDescription,
  DrawerHeader,
  DrawerTitle,
} from "@/components/ui/drawer";
import { Input } from "@/components/ui/input";
import { QueryState } from "@/components/states";

/**
 * The Tracked slide-over: every title on the user's calendar as a poster row — kind, next date (or a
 * "no date yet" marker), in-library / owned-vs-aired hint — with a name filter on top. Adding (TMDb
 * search) and removing live here; a row's bell opens the shared reminder dialog.
 */
export function TrackedDrawer({
  open,
  onOpenChange,
  onAddTitle,
  onRemind,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onAddTitle: () => void;
  onRemind: (item: WatchlistItem) => void;
}) {
  const [filter, setFilter] = useState("");
  const queryClient = useQueryClient();

  const watchlist = useQuery({ queryKey: ["watchlist"], queryFn: watchlistApi.list, enabled: open });

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: ["watchlist"] });
    void queryClient.invalidateQueries({ queryKey: ["watchlist-calendar"] });
    void queryClient.invalidateQueries({ queryKey: ["reminders"] });
  };

  const remove = useMutation({
    mutationFn: (item: WatchlistItem) => watchlistApi.remove(item.id),
    onSuccess: (_, item) => {
      toast.success(`Stopped tracking “${item.title}”`);
      invalidate();
    },
    onError: (error) => toast.error("Couldn’t remove the title", { description: errorMessage(error) }),
  });

  const refresh = useMutation({
    mutationFn: (item: WatchlistItem) => watchlistApi.refresh(item.id),
    onSuccess: () => toast.success("Refreshing dates…", { description: "The calendar updates in a moment." }),
    onError: (error) => toast.error("Couldn’t refresh", { description: errorMessage(error) }),
  });

  const query = filter.trim().toLowerCase();
  const matches = (item: WatchlistItem) => !query || item.title.toLowerCase().includes(query);

  return (
    <Drawer open={open} onOpenChange={onOpenChange} swipeDirection="right">
      <DrawerContent>
        <DrawerHeader>
          <DrawerTitle>Tracked titles</DrawerTitle>
          <DrawerDescription>Movies and series on your release calendar.</DrawerDescription>
        </DrawerHeader>

        <div className="flex min-h-0 flex-1 flex-col gap-3 p-4">
          <div className="flex items-center gap-2">
            <Input
              value={filter}
              placeholder="Filter by name…"
              onChange={(event) => setFilter(event.target.value)}
            />
            <Button variant="secondary" size="sm" onClick={onAddTitle}>
              <CalendarPlus className="size-4" aria-hidden /> Add
            </Button>
          </div>

          <div className="-mr-2 flex min-h-0 flex-1 flex-col gap-1 overflow-y-auto pr-2">
            <QueryState query={watchlist} empty="Nothing tracked yet — add a title to start a calendar.">
              {(items) =>
                items.filter(matches).map((item) => (
                  <TrackedRow
                    key={item.id}
                    item={item}
                    onRemind={() => onRemind(item)}
                    onRefresh={() => refresh.mutate(item)}
                    onRemove={() => remove.mutate(item)}
                    busy={remove.isPending}
                  />
                ))
              }
            </QueryState>
          </div>
        </div>
      </DrawerContent>
    </Drawer>
  );
}

function TrackedRow({
  item,
  onRemind,
  onRefresh,
  onRemove,
  busy,
}: {
  item: WatchlistItem;
  onRemind: () => void;
  onRefresh: () => void;
  onRemove: () => void;
  busy: boolean;
}) {
  const next = item.nextRelease;
  const nextLabel = next
    ? `${next.type === "EpisodeAir" ? (episodeCode(next.season, next.episode) ?? "Episode") : releaseTypeLabel(next.type)} · ${formatDateKey(next.date)}`
    : null;

  return (
    <div className="hover:bg-secondary/60 flex items-center gap-3 rounded-md p-1.5">
      <div className="bg-secondary h-16 w-11 shrink-0 overflow-hidden rounded">
        {item.posterUrl && (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={item.posterUrl} alt="" className="h-full w-full object-cover" />
        )}
      </div>
      <div className="min-w-0 flex-1">
        <p className="flex items-center gap-1.5 truncate font-medium">
          <span className="truncate">{item.title}</span>
          {item.inLibrary && <Check className="text-brand size-3.5 shrink-0" aria-label="In library" />}
        </p>
        <p className="text-muted-foreground truncate text-xs">
          {item.kind === "Movie" ? "Movie" : "Series"}
          {item.year ? ` · ${item.year}` : ""}
        </p>
        <p className="text-muted-foreground flex items-center gap-1 truncate text-xs">
          {nextLabel ?? (
            <>
              <CircleHelp className="size-3 shrink-0" aria-hidden /> No date yet
            </>
          )}
        </p>
        {item.libraryGap && item.libraryGap.missingAired > 0 && (
          <p className="text-brand truncate text-xs">Behind by {item.libraryGap.missingAired} aired</p>
        )}
      </div>
      <div className="flex shrink-0 items-center">
        <Button
          variant="ghost"
          size="icon-sm"
          aria-label="Remind me"
          className={item.reminders.some((reminder) => reminder.active) ? "text-brand" : undefined}
          onClick={onRemind}
        >
          <Bell />
        </Button>
        <Button variant="ghost" size="icon-sm" aria-label="Refresh dates" onClick={onRefresh}>
          <RefreshCw />
        </Button>
        <Button variant="ghost" size="icon-sm" aria-label="Stop tracking" disabled={busy} onClick={onRemove}>
          <Trash2 />
        </Button>
      </div>
    </div>
  );
}
