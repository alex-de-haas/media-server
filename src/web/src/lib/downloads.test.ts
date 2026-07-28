import { describe, expect, it } from "vitest";
import { transferredBytes } from "@/lib/downloads";
import type { Download } from "@/lib/media-server";

const GB = 1024 * 1024 * 1024;

// Only the transfer fields matter here; the rest of a Download is card metadata the readout never reads.
function download(overrides: Partial<Download>): Download {
  return {
    id: "d1",
    infoHash: "abc",
    name: "Escape.from.New.York.1981.mkv",
    catalogId: "c1",
    state: "Downloading",
    keepSeeding: false,
    addedAt: "2026-07-28T10:00:00Z",
    completedAt: null,
    engineState: "Downloading",
    percentComplete: null,
    downloadRateBytesPerSecond: null,
    uploadRateBytesPerSecond: null,
    ratio: null,
    peers: null,
    sizeBytes: null,
    seeds: null,
    leeches: null,
    availablePeers: null,
    downloadedBytes: null,
    uploadedBytes: null,
    remainingBytes: null,
    totalPieces: null,
    completePieces: null,
    etaSeconds: null,
    ...overrides,
  };
}

describe("transferredBytes", () => {
  it("derives what's on disk from the bytes still remaining", () => {
    expect(transferredBytes(download({ sizeBytes: 8 * GB, remainingBytes: 2 * GB }))).toBe(6 * GB);
  });

  it("prefers the remaining-byte derivation over the engine's downloaded total", () => {
    // The engine counts everything pulled off the wire, so a torrent that re-fetched hash-failed pieces
    // reports more downloaded than is on disk. Remaining bytes is the honest signal.
    const overshooting = download({ sizeBytes: 8 * GB, remainingBytes: 2 * GB, downloadedBytes: 7 * GB });
    expect(transferredBytes(overshooting)).toBe(6 * GB);
  });

  it("falls back to the engine's downloaded total when remaining bytes are absent", () => {
    expect(transferredBytes(download({ sizeBytes: 8 * GB, downloadedBytes: 3 * GB }))).toBe(3 * GB);
  });

  it("falls back to the percentage when the engine reports neither byte count", () => {
    expect(transferredBytes(download({ sizeBytes: 8 * GB, percentComplete: 25 }))).toBe(2 * GB);
  });

  it("reads as nothing downloaded when no progress field is reported at all", () => {
    expect(transferredBytes(download({ sizeBytes: 8 * GB }))).toBe(0);
  });

  it("clamps a downloaded total that overshoots the torrent's size", () => {
    // Same wasted-piece drift, on the fallback path where nothing else bounds it.
    expect(transferredBytes(download({ sizeBytes: 8 * GB, downloadedBytes: 9 * GB }))).toBe(8 * GB);
    expect(transferredBytes(download({ sizeBytes: 8 * GB, percentComplete: 101 }))).toBe(8 * GB);
  });

  it("clamps a remaining count larger than the torrent itself", () => {
    // A stale snapshot from before a file-selection change can leave remaining above size — read as 0,
    // never as a negative amount downloaded.
    expect(transferredBytes(download({ sizeBytes: 8 * GB, remainingBytes: 9 * GB }))).toBe(0);
  });

  it("reports a finished torrent as fully transferred", () => {
    expect(transferredBytes(download({ sizeBytes: 8 * GB, remainingBytes: 0 }))).toBe(8 * GB);
  });

  it("hides the readout while the torrent's size is unknown", () => {
    // A magnet that hasn't fetched its metadata yet: null keeps the card from showing "0 B / 0 B".
    expect(transferredBytes(download({ sizeBytes: null, downloadedBytes: 512 }))).toBeNull();
    expect(transferredBytes(download({ sizeBytes: 0, downloadedBytes: 512 }))).toBeNull();
  });
});
