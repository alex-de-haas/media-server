import type { QueryClient } from "@tanstack/react-query";

export interface IndexingStatus {
  state: string;
  percent: number | null;
  revision: number;
}

export function latestIndexing(snapshot?: IndexingStatus | null, live?: IndexingStatus): IndexingStatus | undefined {
  return live && (!snapshot || live.revision > snapshot.revision) ? live : snapshot ?? undefined;
}

export function indexingLabel(status?: IndexingStatus): string | null {
  switch (status?.state) {
    case "waiting": return "Waiting for indexing";
    case "indexing": return status.percent == null ? "Indexing" : `Indexing · ${Math.max(0, Math.min(99, status.percent))}%`;
    case "saving": return "Saving index";
    case "failed": return "Indexing could not finish";
    default: return null;
  }
}

/** Only revisioned indexing payloads enter the cache; older events cannot rewind a newer snapshot. */
export function applyIndexingEvent(client: QueryClient, data: unknown): void {
  if (!data || typeof data !== "object") return;
  const event = data as { sourceId?: unknown; streamId?: unknown; indexing?: IndexingStatus };
  const status = event.indexing;
  if (typeof event.sourceId !== "string" || !status || typeof status.state !== "string"
      || !Number.isSafeInteger(status.revision)
      || (status.percent != null && !Number.isFinite(status.percent))) return;
  const id = typeof event.streamId === "string" ? event.streamId : event.sourceId;
  client.setQueryData<IndexingStatus>(["indexing", id.toLowerCase()], current => latestIndexing(current, status));
}
