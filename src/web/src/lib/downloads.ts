import type { Download } from "@/lib/media-server";

/**
 * Bytes of the torrent's content that are on disk, for the "1.2 GB / 7.5 GB" readout on a download card.
 *
 * Derived from what's left rather than from the engine's `downloadedBytes`: the latter is everything
 * pulled off the wire, so re-requested and hash-failed pieces make it drift past the torrent's size.
 * Falls back to that reported total, then to the percentage, for an engine build that sends neither
 * `remainingBytes` nor a snapshot at all. The result is clamped into the torrent's size so no combination
 * of stale fields can render more downloaded than there is to download.
 *
 * Null while the size is still unknown (a magnet that hasn't fetched its metadata yet), which hides the
 * readout instead of showing "0 B / 0 B".
 */
export function transferredBytes(download: Download): number | null {
  const size = download.sizeBytes;
  if (size == null || size <= 0) {
    return null;
  }
  const done =
    download.remainingBytes != null
      ? size - download.remainingBytes
      : (download.downloadedBytes ?? (size * (download.percentComplete ?? 0)) / 100);
  return Math.min(Math.max(done, 0), size);
}
