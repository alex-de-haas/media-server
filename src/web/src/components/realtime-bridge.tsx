"use client";

import { useEffect } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { openEventStream } from "@/lib/sse";
import type { CatalogRefreshJob, Download, LibraryMoveJob, VpnStatus } from "@/lib/media-server";

// The same-origin BFF route that proxies to the internal `api` SSE endpoint (`/api/events`).
const STREAM_PATH = "/api/proxy/api/events";

// The Job.Type the catalog-wide metadata refresh emits (mirrors CatalogMetadataRefreshService.JobType).
const CATALOG_REFRESH_JOB = "catalog:refresh-metadata";

// The Job.Type a move-between-catalogs emits (mirrors LibraryMoveService.JobType).
const LIBRARY_MOVE_JOB = "library:move";

// Live torrent progress snapshot pushed on every `downloadProgress` event (mirrors the api DownloadProgress).
interface DownloadProgressEvent {
  downloadId: string;
  state: string;
  percentComplete: number;
  downloadRateBytesPerSecond: number;
  uploadRateBytesPerSecond: number;
  ratio: number;
  peers: number;
  sizeBytes: number;
  etaSeconds: number | null;
  seeds: number | null;
  leeches: number | null;
  availablePeers: number | null;
  downloadedBytes: number | null;
  uploadedBytes: number | null;
  remainingBytes: number | null;
  totalPieces: number | null;
  completePieces: number | null;
}

interface IngestStageEvent {
  status: string;
}

// Background-job lifecycle event (mirrors the api JobEvent). `relatedId` is the catalog id for a
// catalog-wide metadata refresh. `bytesPerSecond`/`etaSeconds` ride along on a move's progress tick (the
// live copy throughput) and are null for other jobs and for the start/complete/fail events.
interface JobEvent {
  jobId: string;
  type: string;
  relatedId: string | null;
  status: string;
  progress: number;
  bytesPerSecond: number | null;
  etaSeconds: number | null;
}

/**
 * Bridges the server's realtime stream into the React Query cache so the activity/downloads views update
 * without polling: high-frequency progress is patched directly into the `downloads` cache, and coarser
 * transitions invalidate the affected queries. Mounted once inside the authenticated shell. The slow
 * fallback `refetchInterval`s on those queries cover the case where the stream can't connect.
 */
export function RealtimeBridge() {
  useRealtime();
  return null;
}

function useRealtime() {
  const queryClient = useQueryClient();

  useEffect(() => {
    return openEventStream(STREAM_PATH, {
      onEvent: (event, data) => handleEvent(queryClient, event, data),
      // On (re)connect, reconcile anything missed while disconnected.
      onStatus: (connected) => {
        if (connected) {
          invalidate(queryClient, ["downloads"], ["ingest"], ["vpn"], ["catalog-refresh-jobs"], ["library-move-jobs"]);
        }
      },
    });
  }, [queryClient]);
}

function handleEvent(queryClient: QueryClient, event: string, data: unknown): void {
  switch (event) {
    case "downloadProgress":
      patchDownload(queryClient, data as DownloadProgressEvent);
      break;
    case "downloadStateChanged":
      invalidate(queryClient, ["downloads"], ["ingest"]);
      break;
    case "ingestStageChanged":
      invalidate(queryClient, ["ingest"]);
      // A published item changes the library, its collections, and the Home rails.
      if ((data as IngestStageEvent).status === "Done") {
        invalidate(queryClient, ["library"], ["collections"], ["recent"], ["resume"], ["nextup"]);
      }
      break;
    case "vpnStatusChanged":
      // Engine-wide status — patch the cache directly (the event payload mirrors VpnStatus).
      queryClient.setQueryData<VpnStatus>(["vpn"], data as VpnStatus);
      break;
    case "jobStarted":
    case "jobProgress":
    case "jobCompleted":
    case "jobFailed":
      handleJobEvent(queryClient, event, data as JobEvent);
      break;
  }
}

// Catalog refresh jobs drive their own UI (a progress bar on the catalog row); a move relocates an item
// between catalogs; every other job type just nudges the activity view to refetch.
function handleJobEvent(queryClient: QueryClient, event: string, job: JobEvent): void {
  if (job.type === CATALOG_REFRESH_JOB && job.relatedId) {
    patchCatalogRefresh(queryClient, event, job);
    return;
  }

  if (job.type === LIBRARY_MOVE_JOB) {
    patchLibraryMove(queryClient, event, job);
    return;
  }

  invalidate(queryClient, ["ingest"]);
}

// Keep the ["library-move-jobs"] cache in step with the live move: upsert its progress while running, drop it
// when it finishes. The JobEvent carries no title/target, so on start pull the enriched active list; on
// completion the item changed catalog (and, on a merge, its id), so refresh the library views and its detail.
function patchLibraryMove(queryClient: QueryClient, event: string, job: JobEvent): void {
  const done = event === "jobCompleted" || event === "jobFailed";
  const jobs = queryClient.getQueryData<LibraryMoveJob[]>(["library-move-jobs"]) ?? [];
  const existing = jobs.find((entry) => entry.jobId === job.jobId);
  const others = jobs.filter((entry) => entry.jobId !== job.jobId);

  queryClient.setQueryData<LibraryMoveJob[]>(
    ["library-move-jobs"],
    done
      ? others
      : [
          ...others,
          // Preserve the labels a prior seed/tick resolved — the event itself doesn't carry them. Speed/ETA
          // ride on each progress tick; keep the last-known values between ticks (a jobStarted carries none).
          {
            itemId: job.relatedId ?? existing?.itemId ?? "",
            jobId: job.jobId,
            progress: job.progress,
            title: existing?.title ?? null,
            targetCatalogName: existing?.targetCatalogName ?? null,
            bytesPerSecond: job.bytesPerSecond ?? existing?.bytesPerSecond ?? null,
            etaSeconds: job.etaSeconds ?? existing?.etaSeconds ?? null,
          },
        ],
  );

  // A just-started move has no labels in the cache yet; refetch the active list to fill in what's moving where.
  if (event === "jobStarted") {
    invalidate(queryClient, ["library-move-jobs"]);
  }

  if (done) {
    invalidate(queryClient, ["library"], ["collections"], ["recent"], ["resume"], ["nextup"], ["ingest"]);
    // relatedId is the resulting top-level item; on a merge it differs from the item the move started on,
    // whose (now deleted) detail page must also learn its fate — refresh both.
    if (job.relatedId) {
      queryClient.invalidateQueries({ queryKey: ["library-detail", job.relatedId] });
    }
    if (existing && existing.itemId !== job.relatedId) {
      queryClient.invalidateQueries({ queryKey: ["library-detail", existing.itemId] });
    }
  }
}

// Keep the ["catalog-refresh-jobs"] cache in step with the live job: upsert while running, drop when it
// finishes. On completion the catalog's metadata changed, so refresh the library views that render it.
function patchCatalogRefresh(queryClient: QueryClient, event: string, job: JobEvent): void {
  const catalogId = job.relatedId!;
  const done = event === "jobCompleted" || event === "jobFailed";

  queryClient.setQueryData<CatalogRefreshJob[]>(["catalog-refresh-jobs"], (jobs = []) => {
    const others = jobs.filter((entry) => entry.catalogId !== catalogId);
    return done ? others : [...others, { catalogId, jobId: job.jobId, progress: job.progress }];
  });

  if (event === "jobCompleted") {
    invalidate(queryClient, ["library"], ["collections"], ["recent"], ["resume"], ["nextup"]);
  }
}

// Patch live transfer fields onto the matching download in place — no refetch for high-frequency progress.
function patchDownload(queryClient: QueryClient, progress: DownloadProgressEvent): void {
  queryClient.setQueryData<Download[]>(["downloads"], (downloads) =>
    downloads?.map((download) =>
      download.id === progress.downloadId
        ? {
            ...download,
            engineState: progress.state,
            percentComplete: progress.percentComplete,
            downloadRateBytesPerSecond: progress.downloadRateBytesPerSecond,
            uploadRateBytesPerSecond: progress.uploadRateBytesPerSecond,
            ratio: progress.ratio,
            peers: progress.peers,
            sizeBytes: progress.sizeBytes,
            etaSeconds: progress.etaSeconds,
            seeds: progress.seeds,
            leeches: progress.leeches,
            availablePeers: progress.availablePeers,
            downloadedBytes: progress.downloadedBytes,
            uploadedBytes: progress.uploadedBytes,
            remainingBytes: progress.remainingBytes,
            totalPieces: progress.totalPieces,
            completePieces: progress.completePieces,
          }
        : download,
    ),
  );
}

function invalidate(queryClient: QueryClient, ...keys: string[][]): void {
  for (const queryKey of keys) {
    queryClient.invalidateQueries({ queryKey });
  }
}
