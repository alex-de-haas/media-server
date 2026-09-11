"use client";

import { skipToken, useQuery } from "@tanstack/react-query";
import { LoaderCircle } from "lucide-react";
import { indexingLabel, latestIndexing, type IndexingStatus } from "@/lib/indexing";

/** Snapshot on entry, SSE thereafter. This component never starts a timer or a connection. */
export function IndexingIndicator({ id, snapshot }: { id: string; snapshot?: IndexingStatus | null }) {
  const { data: live } = useQuery<IndexingStatus>({ queryKey: ["indexing", id.toLowerCase()], queryFn: skipToken });
  const { data: connected } = useQuery<boolean>({ queryKey: ["indexing-connected"], queryFn: skipToken });
  const status = latestIndexing(snapshot, live);
  const label = indexingLabel(status);
  if (!label || !status) return null;
  const determinate = status.state === "indexing" && status.percent != null;
  return (
    <span className="my-2 flex w-64 max-w-full flex-col gap-1 text-xs" role="status">
      <span className="flex items-center gap-2">
        {!determinate && status.state !== "failed" && <LoaderCircle className="size-3 animate-spin" aria-hidden />}
        {label}{connected === false && <span className="text-muted-foreground"> · Reconnecting…</span>}
      </span>
      {determinate && (
        <span role="progressbar" aria-label="Indexing progress" aria-valuemin={0} aria-valuemax={100}
          aria-valuenow={Math.max(0, Math.min(99, status.percent!))} className="bg-muted h-1.5 w-full overflow-hidden rounded-full">
          <span className="bg-primary block h-full" style={{ width: `${Math.max(0, Math.min(99, status.percent!))}%` }} />
        </span>
      )}
    </span>
  );
}
