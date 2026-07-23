// Pure logic behind the Watch history providers Settings surface, kept out of the components so the
// OAuth polling decisions, sync-scope shaping, and preview/apply gating can be unit-tested without a
// DOM. The components stay thin wrappers over these.

import type {
  WatchHistoryAuthorizationState,
  WatchHistoryConnection,
  WatchHistorySyncPreview,
  WatchHistorySyncScope,
} from "@/lib/media-server";

// The cadence to poll at when the provider names none. Trakt's device flow asks for ~5s.
export const DEFAULT_POLL_MS = 5000;

/** What to do after one Device OAuth poll, given the reported state and any retry hint. */
export type PollStep =
  | { kind: "approved" }
  | { kind: "denied" }
  | { kind: "expired" }
  | { kind: "wait"; delayMs: number };

/**
 * Decides the next step of the device flow from a poll result. Pending and SlowDown both mean "keep
 * waiting"; the only difference is the interval, which the provider raises on SlowDown and we honor
 * through <c>pollIntervalSeconds</c>. Anything unrecognized is treated as "keep waiting" so a new
 * provider state never strands the loop.
 */
export function nextPollStep(
  state: WatchHistoryAuthorizationState,
  pollIntervalSeconds: number | null,
): PollStep {
  switch (state) {
    case "Approved":
      return { kind: "approved" };
    case "Denied":
      return { kind: "denied" };
    case "Expired":
      return { kind: "expired" };
    case "SlowDown":
    case "Pending":
    default:
      return { kind: "wait", delayMs: (pollIntervalSeconds ?? DEFAULT_POLL_MS / 1000) * 1000 };
  }
}

/**
 * Shapes the sync scope the preview endpoint expects. Empty on an axis means "all", so a fully
 * selected (or fully cleared) kind set sends nothing on that axis rather than an explicit pair — the
 * two are equivalent to the backend, and omitting it keeps the request honest about intent.
 */
export function buildScopeRequest(
  includeMovies: boolean,
  includeEpisodes: boolean,
  catalogIds: string[],
): WatchHistorySyncScope {
  const kinds: Array<"Movie" | "Episode"> = [];
  if (includeMovies) kinds.push("Movie");
  if (includeEpisodes) kinds.push("Episode");
  return {
    catalogIds: catalogIds.length > 0 ? catalogIds : undefined,
    kinds: kinds.length === 1 ? kinds : undefined,
  };
}

/** A preview needs at least one media kind selected to compare anything. */
export function canPreview(includeMovies: boolean, includeEpisodes: boolean): boolean {
  return includeMovies || includeEpisodes;
}

/** True while local changes are still queued for the provider, which apply refuses to run over. */
export function hasBlockingWork(preview: WatchHistorySyncPreview): boolean {
  return preview.hasPendingOutboundWork || preview.hasTerminalOutboundWork;
}

/** How many items apply would actually move — the import and export tallies. */
export function changeCount(preview: WatchHistorySyncPreview): number {
  return (preview.counts.RemoteOnly ?? 0) + (preview.counts.LocalOnly ?? 0);
}

/** Apply is offered only when something would move and nothing local is still in flight. */
export function canApply(preview: WatchHistorySyncPreview): boolean {
  return !hasBlockingWork(preview) && changeCount(preview) > 0;
}

/** Which badge the provider card shows for a connection (or the absence of one). */
export function connectionBadge(
  connection: WatchHistoryConnection | null,
): "connected" | "reconnect" | "none" {
  if (!connection) {
    return "none";
  }
  return connection.status === "RequiresReconnect" ? "reconnect" : "connected";
}
