"use client";

import { useState } from "react";
import { useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { CalendarPlus, Clock, Trash2 } from "lucide-react";
import { mediaServer, type MovieWatchEntry } from "@/lib/media-server";
import { QUERIES_AFFECTED_BY_HISTORY_CHANGE } from "@/lib/watch-history-calendar";
import { errorMessage } from "@/lib/ui";
import { toast } from "@/lib/toast";
import { Button } from "@/components/ui/button";
import { ScrollArea, ScrollBar } from "@/components/ui/scroll-area";
import { Timeline, TimelineContent, TimelineHeader, TimelineIndicator, TimelineItem, TimelineSeparator, TimelineTitle } from "@/components/reui/timeline";
import { WatchTimeDialog } from "@/components/watch-time-dialog";
import { DeleteWatchDialog } from "@/components/delete-watch-dialog";

function watchLabel(entry: MovieWatchEntry) {
  return entry.watchedAt
    ? new Date(entry.watchedAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })
    : "Date unknown";
}

export function MovieWatchHistory({ id, title, removed }: { id: string; title: string; removed: boolean }) {
  const [expanded, setExpanded] = useState(false);
  const [editing, setEditing] = useState<MovieWatchEntry | "new" | null>(null);
  const [deleting, setDeleting] = useState<MovieWatchEntry | null>(null);
  const client = useQueryClient();
  const history = useInfiniteQuery({
    queryKey: ["movie-watch-history", id],
    initialPageParam: 0,
    queryFn: ({ pageParam }) => mediaServer.movieWatchHistory(id, pageParam, 20),
    getNextPageParam: (last) => last.offset + last.entries.length < last.datedTotal
      ? last.offset + last.entries.length : undefined,
  });
  const invalidate = () => {
    for (const queryKey of QUERIES_AFFECTED_BY_HISTORY_CHANGE) void client.invalidateQueries({ queryKey });
  };
  const save = useMutation({
    mutationFn: async ({ entry, watchedAt }: { entry: MovieWatchEntry | "new"; watchedAt: string }) => {
      if (entry === "new") await mediaServer.logWatch(id, watchedAt);
      else await mediaServer.setWatchHistoryEntryTime(entry.id, watchedAt);
    },
    onSuccess: () => { setEditing(null); invalidate(); toast.success("Watch saved"); },
    onError: (error) => toast.error("Couldn’t save this watch", { description: errorMessage(error) }),
  });
  const remove = useMutation({
    mutationFn: (entry: MovieWatchEntry) => mediaServer.deleteWatchHistoryEntry(entry.id),
    onSuccess: () => { setDeleting(null); invalidate(); toast.success("Play deleted"); },
    onError: (error) => toast.error("Couldn’t delete this play", { description: errorMessage(error) }),
  });
  const first = history.data?.pages[0];
  const dated = history.data?.pages.flatMap((page) => page.entries) ?? [];
  const visibleDated = expanded ? dated : dated.slice(0, 3);
  const undated = first?.undated ?? [];
  const actions = (entry: MovieWatchEntry) => (
    <div className="flex shrink-0 gap-1">
      <Button variant="ghost" size="icon-sm" aria-label={`${entry.watchedAt ? "Change" : "Set"} time for ${title}, ${watchLabel(entry)}`} onClick={() => setEditing(entry)}>
        <Clock aria-hidden />
      </Button>
      <Button variant="ghost" size="icon-sm" aria-label={`Delete viewing of ${title}, ${watchLabel(entry)}`} onClick={() => setDeleting(entry)}>
        <Trash2 aria-hidden />
      </Button>
    </div>
  );

  if (first && first.total < 2) return null;

  return (
    <section aria-label="Watch history" className="flex min-w-0 flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-lg font-semibold tracking-tight">Watch history{first ? ` · ${first.total}` : ""}</h2>
        <Button variant="ghost" size="sm" onClick={() => setEditing("new")}>
          <CalendarPlus data-icon="inline-start" aria-hidden /> Log watch
        </Button>
      </div>
      {history.isPending && <p className="text-muted-foreground text-sm">Loading history…</p>}
      {history.isError && (
        <p className="text-muted-foreground text-sm">Couldn’t load history. <Button variant="link" onClick={() => void history.refetch()}>Retry</Button></p>
      )}
      {first?.total === 0 && <p className="text-muted-foreground text-sm">No recorded watches yet.</p>}
      {visibleDated.length > 0 && (
        <ScrollArea className="min-w-0 w-full">
          <Timeline orientation="horizontal" value={visibleDated.length} render={<ol aria-label="Dated watches" />} className="min-w-max pb-3">
            {visibleDated.map((entry, index) => (
              <TimelineItem key={entry.id} step={index + 1} render={<li />} className="min-w-48">
                <TimelineHeader>
                  <TimelineTitle render={<time dateTime={entry.watchedAt!} />}>
                    {new Date(entry.watchedAt!).toLocaleDateString(undefined, { dateStyle: "medium" })}
                  </TimelineTitle>
                </TimelineHeader>
                <TimelineIndicator />
                {index < visibleDated.length - 1 && <TimelineSeparator />}
                <TimelineContent className="flex items-center gap-3">
                  <time dateTime={entry.watchedAt!}>
                    {new Date(entry.watchedAt!).toLocaleTimeString(undefined, { timeStyle: "short" })}
                  </time>
                  {actions(entry)}
                </TimelineContent>
              </TimelineItem>
            ))}
          </Timeline>
          <ScrollBar orientation="horizontal" />
        </ScrollArea>
      )}
      {undated.length > 0 && (
        <ul aria-label="Watches with unknown dates" className="divide-y">
          {undated.map((entry) => (
            <li key={entry.id} className="flex items-center justify-between gap-3 py-1.5 text-sm">
              <span>Date unknown</span>
              {actions(entry)}
            </li>
          ))}
        </ul>
      )}
      {(first?.datedTotal ?? 0) > 3 && (
        <div className="flex gap-2">
          <Button variant="ghost" size="sm" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>
            {expanded ? "Show less" : `Show all ${first!.datedTotal} dated watches`}
          </Button>
          {expanded && history.hasNextPage && (
            <Button variant="ghost" size="sm" disabled={history.isFetchingNextPage} onClick={() => void history.fetchNextPage()}>
              {history.isFetchingNextPage ? "Loading…" : "Load more"}
            </Button>
          )}
        </div>
      )}
      <WatchTimeDialog
        open={editing !== null} onOpenChange={(open) => !open && setEditing(null)}
        heading={editing === "new" ? "Log a watch" : "When did you watch it?"}
        description={editing === "new" ? `Record a viewing of ${title}.` : `Correct the date and time of this viewing of ${title}. Your play count does not change.`}
        confirmLabel={editing === "new" ? "Log watch" : "Save time"}
        initialInstant={editing && editing !== "new" ? editing.watchedAt : null}
        pending={save.isPending} onSubmit={(watchedAt) => editing && save.mutate({ entry: editing, watchedAt })}
      />
      <DeleteWatchDialog open={deleting !== null} onOpenChange={(open) => !open && setDeleting(null)}
        title={title} detail={deleting && watchLabel(deleting)} removed={removed} pending={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting)} />
    </section>
  );
}
