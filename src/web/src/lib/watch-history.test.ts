import { describe, expect, it } from "vitest";
import type { WatchHistorySyncPreview } from "@/lib/media-server";
import {
  buildScopeRequest,
  canApply,
  canPreview,
  changeCount,
  connectionBadge,
  DEFAULT_POLL_MS,
  hasBlockingWork,
  nextPollStep,
} from "@/lib/watch-history";

function preview(overrides: Partial<WatchHistorySyncPreview> = {}): WatchHistorySyncPreview {
  return {
    runId: "run-1",
    counts: {},
    sample: [],
    hasPendingOutboundWork: false,
    hasTerminalOutboundWork: false,
    aggregateCountsMayCollapse: false,
    ...overrides,
  };
}

describe("nextPollStep", () => {
  it("finishes on approval", () => {
    expect(nextPollStep("Approved", null)).toEqual({ kind: "approved" });
  });

  it("stops on a decline or an expiry rather than looping forever", () => {
    expect(nextPollStep("Denied", 5)).toEqual({ kind: "denied" });
    expect(nextPollStep("Expired", 5)).toEqual({ kind: "expired" });
  });

  it("keeps waiting on Pending at the default cadence when none is given", () => {
    expect(nextPollStep("Pending", null)).toEqual({ kind: "wait", delayMs: DEFAULT_POLL_MS });
  });

  it("backs off to the provider's interval on SlowDown", () => {
    expect(nextPollStep("SlowDown", 30)).toEqual({ kind: "wait", delayMs: 30000 });
  });

  it("treats an unknown state as keep-waiting so the loop is never stranded", () => {
    expect(nextPollStep("Something" as never, 8)).toEqual({ kind: "wait", delayMs: 8000 });
  });
});

describe("buildScopeRequest", () => {
  it("omits both axes when everything is selected — empty means all", () => {
    expect(buildScopeRequest(true, true, [])).toEqual({ catalogIds: undefined, kinds: undefined });
  });

  it("sends the single kind when only one is selected", () => {
    expect(buildScopeRequest(true, false, [])).toEqual({ catalogIds: undefined, kinds: ["Movie"] });
    expect(buildScopeRequest(false, true, [])).toEqual({ catalogIds: undefined, kinds: ["Episode"] });
  });

  it("sends catalog ids only when the user narrowed them", () => {
    expect(buildScopeRequest(true, true, ["a", "b"]).catalogIds).toEqual(["a", "b"]);
  });

  it("omits kinds when neither is selected (guarded by canPreview upstream)", () => {
    expect(buildScopeRequest(false, false, []).kinds).toBeUndefined();
  });
});

describe("canPreview", () => {
  it("requires at least one media kind", () => {
    expect(canPreview(false, false)).toBe(false);
    expect(canPreview(true, false)).toBe(true);
    expect(canPreview(false, true)).toBe(true);
  });
});

describe("apply gating", () => {
  it("blocks while local work is still queued for the provider", () => {
    expect(hasBlockingWork(preview({ hasPendingOutboundWork: true }))).toBe(true);
    expect(hasBlockingWork(preview({ hasTerminalOutboundWork: true }))).toBe(true);
    expect(hasBlockingWork(preview())).toBe(false);
  });

  it("counts only the imports and exports as changes", () => {
    expect(changeCount(preview({ counts: { RemoteOnly: 3, LocalOnly: 2, InSync: 9 } }))).toBe(5);
  });

  it("offers apply only when something moves and nothing is in flight", () => {
    expect(canApply(preview({ counts: { RemoteOnly: 1 } }))).toBe(true);
    // Nothing to move.
    expect(canApply(preview({ counts: { InSync: 4 } }))).toBe(false);
    // Something to move, but local work would be undone by importing the older snapshot.
    expect(canApply(preview({ counts: { RemoteOnly: 1 }, hasPendingOutboundWork: true }))).toBe(false);
  });
});

describe("connectionBadge", () => {
  it("distinguishes connected, reconnect, and absent", () => {
    expect(connectionBadge(null)).toBe("none");
    expect(
      connectionBadge({
        providerKey: "trakt",
        status: "Connected",
        accountName: "alex",
        connectedAt: "2026-07-23T00:00:00Z",
        lastDeliveryAt: null,
        lastSyncAt: null,
        lastError: null,
      }),
    ).toBe("connected");
    expect(
      connectionBadge({
        providerKey: "trakt",
        status: "RequiresReconnect",
        accountName: "alex",
        connectedAt: "2026-07-23T00:00:00Z",
        lastDeliveryAt: null,
        lastSyncAt: null,
        lastError: "Trakt rejected the stored refresh token.",
      }),
    ).toBe("reconnect");
  });
});
