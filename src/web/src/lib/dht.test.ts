import { describe, expect, it } from "vitest";
import { dhtKind, dhtLabel, dhtTooltip } from "@/lib/dht";
import type { DhtStatus } from "@/lib/media-server";

function status(overrides: Partial<DhtStatus> = {}): DhtStatus {
  return { enabled: true, running: true, state: "Ready", nodeCount: 42, ...overrides };
}

describe("dhtKind", () => {
  it("hides the indicator when there is nothing to report", () => {
    // Downloading disabled, or a torrent-engine too old to have /dht.
    expect(dhtKind(null)).toBeNull();
  });

  it("hides the indicator while DHT is idle", () => {
    // The engine is recycled when nothing is downloading; that is not a fault worth a badge.
    expect(dhtKind(status({ running: false, state: null, nodeCount: 0 }))).toBeNull();
  });

  it("reports a deliberately disabled DHT as off", () => {
    expect(dhtKind(status({ enabled: false, running: false, state: null, nodeCount: 0 }))).toBe("off");
  });

  it("reports Initialising as starting, not broken", () => {
    // The whole point: a healthy bootstrap must not be shown as a failure.
    expect(dhtKind(status({ state: "Initialising", nodeCount: 0 }))).toBe("starting");
  });

  it("reports NotReady as broken", () => {
    expect(dhtKind(status({ state: "NotReady", nodeCount: 0 }))).toBe("broken");
  });

  it("reports Ready as working", () => {
    expect(dhtKind(status())).toBe("ready");
  });
});

describe("dhtLabel", () => {
  it("shows the node count when working", () => {
    expect(dhtLabel(status({ nodeCount: 87 }), "ready")).toBe("DHT · 87");
  });

  it("names the non-working states without a count", () => {
    expect(dhtLabel(status(), "off")).toBe("DHT off");
    expect(dhtLabel(status(), "starting")).toBe("DHT starting");
    expect(dhtLabel(status(), "broken")).toBe("DHT no peers");
  });
});

describe("dhtTooltip", () => {
  it("explains the consequence when DHT is not working", () => {
    expect(dhtTooltip(status({ state: "NotReady", nodeCount: 0 }), "broken")).toContain("magnet links");
  });

  it("pluralises the node count", () => {
    expect(dhtTooltip(status({ nodeCount: 1 }), "ready")).toContain("1 node in");
    expect(dhtTooltip(status({ nodeCount: 2 }), "ready")).toContain("2 nodes in");
  });
});
