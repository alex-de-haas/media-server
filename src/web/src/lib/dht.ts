import type { DhtStatus } from "@/lib/media-server";

// How the Activity header presents DHT health. `null` means show nothing at all.
export type DhtKind = "off" | "starting" | "broken" | "ready";

// Which of the four presentations a status maps to, or null to hide the indicator.
//
// Hidden when there is nothing to report (downloading disabled, or a torrent-engine older than 0.7.0 with
// no /dht endpoint) and while DHT is enabled but not running — the engine is recycled when nothing is
// downloading, so "idle" would only repeat what an empty activity list already says.
//
// The distinction that matters: `NotReady` while running means DHT is on but never found a peer, so magnet
// links without trackers quietly fail. `Initialising` is a healthy start-up and must not read as broken —
// deriving failure from `state !== "Ready"` would flag every bootstrap.
//
// Every state is matched explicitly, and an unrecognised one hides the badge rather than falling through
// to "ready": `state` crosses an app boundary from an independently-versioned engine, and a green
// all-clear is the one answer that must never be guessed.
export function dhtKind(status: DhtStatus | null): DhtKind | null {
  if (!status) return null;
  if (!status.enabled) return "off";
  if (!status.running) return null;

  switch (status.state) {
    case "Ready":
      return "ready";
    case "Initialising":
      return "starting";
    case "NotReady":
      return "broken";
    default:
      return null;
  }
}

export function dhtLabel(status: DhtStatus, kind: DhtKind): string {
  switch (kind) {
    case "off":
      return "DHT off";
    case "starting":
      return "DHT starting";
    case "broken":
      return "DHT no peers";
    case "ready":
      return `DHT · ${status.nodeCount}`;
  }
}

export function dhtTooltip(status: DhtStatus, kind: DhtKind): string {
  switch (kind) {
    case "off":
      return "DHT is switched off in the torrent engine — peers come from trackers, PEX and local discovery only.";
    case "starting":
      return "DHT is starting up.";
    case "broken":
      return "DHT is enabled but has not found any peers, so magnet links without trackers won't resolve. Its routing table is empty.";
    case "ready":
      return `DHT is working — ${status.nodeCount} node${status.nodeCount === 1 ? "" : "s"} in the routing table.`;
  }
}
