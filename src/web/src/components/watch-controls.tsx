"use client";

import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { CalendarPlus, Check, X } from "lucide-react";
import { mediaServer, type UserItemData } from "@/lib/media-server";
import { QUERIES_AFFECTED_BY_HISTORY_CHANGE } from "@/lib/watch-history-calendar";
import { errorMessage } from "@/lib/ui";
import { toast } from "@/lib/toast";
import { Button } from "@/components/ui/button";
import { WatchTimeDialog } from "@/components/watch-time-dialog";

export function WatchControls({ id, title, userData, removed = false, compact = false, statusOnly = false }: {
  id: string;
  title: string;
  userData: UserItemData | null;
  removed?: boolean;
  compact?: boolean;
  statusOnly?: boolean;
}) {
  const [logging, setLogging] = useState(false);
  const client = useQueryClient();
  const invalidate = () => {
    for (const queryKey of QUERIES_AFFECTED_BY_HISTORY_CHANGE) void client.invalidateQueries({ queryKey });
  };
  const mutation = useMutation({
    mutationFn: (action: "finish" | "dismiss" | { watchedAt: string }) =>
      action === "finish" ? mediaServer.finishWatching(id)
        : action === "dismiss" ? mediaServer.dismissResume(id) : mediaServer.logWatch(id, action.watchedAt),
    onSuccess: (_, action) => {
      setLogging(false);
      invalidate();
      toast.success(action === "dismiss" ? "Removed from Continue watching" : "Watch saved");
    },
    onError: (error) => toast.error(errorMessage(error)),
  });
  const started = !removed && (userData?.playbackPositionTicks ?? 0) > 0;
  const watchedAt = userData?.lastWatchedAt;
  const watched = userData?.played || !!watchedAt;
  return (
    <div className="flex min-w-0 max-w-full flex-wrap items-center gap-2">
      {!compact && <span className="text-muted-foreground inline-flex flex-wrap items-center gap-1.5 text-sm" aria-label="Watch status">
        {watched && <Check className="size-4" aria-hidden />}
        {watched ? "Watched" : "Not watched"}
        {watchedAt && <time dateTime={watchedAt}>· {new Date(watchedAt).toLocaleDateString(undefined, { dateStyle: "medium" })}</time>}
      </span>}
      {!statusOnly && <>
        {started ? <>
          <Button variant="outline" size={compact ? "icon-sm" : "sm"} title="Finish watching" aria-label={`Finish watching ${title}`} disabled={mutation.isPending} onClick={() => mutation.mutate("finish")}>
            <Check data-icon="inline-start" />{!compact && "Finish watching"}
          </Button>
          <Button variant="ghost" size={compact ? "icon-sm" : "sm"} aria-label={`Remove ${title} from Continue watching`} title="Remove from Continue watching" disabled={mutation.isPending} onClick={() => mutation.mutate("dismiss")}>
            <X data-icon="inline-start" />{!compact && "Clear progress"}
          </Button>
        </> : <Button variant="outline" size="sm" onClick={() => setLogging(true)}>
          <CalendarPlus data-icon="inline-start" /> Log watch
        </Button>}
        <WatchTimeDialog open={logging} onOpenChange={setLogging} heading="Log a watch" description={`Record a viewing of ${title}.`} confirmLabel="Log watch" pending={mutation.isPending} onSubmit={(watchedAt) => mutation.mutate({ watchedAt })} />
      </>}
    </div>
  );
}
