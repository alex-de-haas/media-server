"use client";

import { useEffect } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { openEventStream } from "@/lib/sse";
import type { Download, VpnStatus } from "@/lib/media-server";

// The same-origin BFF route that proxies to the internal `api` SSE endpoint (`/api/events`).
const STREAM_PATH = "/api/proxy/api/events";

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
}

interface IngestStageEvent {
  status: string;
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
          invalidate(queryClient, ["downloads"], ["ingest"], ["vpn"]);
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
      invalidate(queryClient, ["ingest"]);
      break;
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
