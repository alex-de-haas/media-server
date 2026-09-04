export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || bytes <= 0) {
    return "0 B";
  }
  const units = ["B", "KB", "MB", "GB", "TB"];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / Math.pow(1024, exponent);
  return `${value.toFixed(value >= 10 || exponent === 0 ? 0 : 1)} ${units[exponent]}`;
}

export function formatSpeed(bytesPerSecond: number | null | undefined): string {
  if (!bytesPerSecond) {
    return "—";
  }
  return `${formatBytes(bytesPerSecond)}/s`;
}

export function formatPercent(value: number | null | undefined): string {
  return value == null ? "—" : `${value.toFixed(1)}%`;
}

// .NET runtime ticks (100ns units) → "1h 56m" / "42m". Null when unknown.
export function formatRuntime(ticks: number | null | undefined): string | null {
  if (!ticks || ticks <= 0) {
    return null;
  }
  const totalMinutes = Math.round(ticks / 600_000_000);
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`;
}

/**
 * The zero-padded episode code shown beside an episode title: "S01E03", or "S01E01-E02" when a single
 * file holds a consecutive range (a "double episode" — one library item, no separate item for the second
 * episode). Follows the same range convention as the on-disk names the organizer writes.
 */
export function episodeLabel(
  seasonNumber: number | null | undefined,
  episodeNumber: number | null | undefined,
  episodeNumberEnd?: number | null,
): string {
  const season = String(seasonNumber ?? 0).padStart(2, "0");
  const episode = String(episodeNumber ?? 0).padStart(2, "0");
  const end = episodeNumberEnd != null && episodeNumberEnd > (episodeNumber ?? 0) ? episodeNumberEnd : null;
  return end == null ? `S${season}E${episode}` : `S${season}E${episode}-E${String(end).padStart(2, "0")}`;
}

// ISO timestamp → coarse "just now" / "5m ago" / "3h ago" / "2d ago". Null when unparseable.
export function formatTimeAgo(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) {
    return null;
  }
  const seconds = Math.max(0, Math.floor((Date.now() - then) / 1000));
  if (seconds < 45) {
    return "just now";
  }
  // Below 60s we already returned "just now"; clamp so 45–59s reads "1m ago", never "0m ago".
  const minutes = Math.max(1, Math.floor(seconds / 60));
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }
  return `${Math.floor(hours / 24)}d ago`;
}

export function formatEta(seconds: number | null | undefined): string {
  if (seconds == null || seconds <= 0 || !Number.isFinite(seconds)) {
    return "—";
  }
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = Math.floor(seconds % 60);
  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }
  if (minutes > 0) {
    return `${minutes}m ${secs}s`;
  }
  return `${secs}s`;
}

/** The object-based audio format a track carries, from the probe's codec profile — "Atmos" or "DTS:X", null
 * for everything else. Worth singling out because it is the one audio fact that decides what may be done to
 * a track: neither format survives a re-encode (ffmpeg encodes no JOC and no DTS:X), so an Atmos track is
 * one to copy, and nothing else on the row says so — "TrueHD 7.1" looks identical either way.
 *
 * Reads the profile rather than the codec: ffprobe reports `truehd` for both, and puts "Dolby TrueHD +
 * Dolby Atmos" in the profile. A file probed from container headers alone carries no profile at all, so
 * this answers null there — absent, not "no Atmos". */
export function objectAudioFormat(profile: string | null | undefined): string | null {
  if (!profile) {
    return null;
  }
  if (/atmos/i.test(profile)) {
    return "Atmos";
  }
  return /dts:?x/i.test(profile) ? "DTS:X" : null;
}

import type { DolbyVisionDetail } from "@/lib/media-server";

/** How a Dolby Vision profile is named on a badge: "Dolby Vision 8.1" for profile 8 by its base layer, the
 *  bare profile otherwise ("Dolby Vision 7", "Dolby Vision 5"). The level is left out — it is 6 on nearly
 *  every film and tells a viewer nothing the profile does not. */
export function dolbyVisionLabel(detail: DolbyVisionDetail | null | undefined): string {
  if (!detail) {
    return "Dolby Vision";
  }
  return detail.profile === 8 ? `Dolby Vision 8.${detail.blCompatibilityId}` : `Dolby Vision ${detail.profile}`;
}

/** The dynamic-range badges for a video stream: one per format the probe named, so a value like
 *  "Dolby Vision · HDR10" (what a profile 8.1 file honestly is) yields two, with the Dolby Vision one
 *  carrying the profile when it is recorded. Nothing for SDR or an unknown range — a missing badge beats a
 *  false one. */
export function dynamicRangeBadges(hdrFormat: string | null | undefined, detail: DolbyVisionDetail | null | undefined): string[] {
  if (!hdrFormat) {
    return [];
  }
  return hdrFormat
    .split(/[·,]/)
    .map((part) => part.trim())
    .filter((part) => part.length > 0 && part.toUpperCase() !== "SDR")
    .map((part) => (/dolby vision/i.test(part) ? dolbyVisionLabel(detail) : part));
}

/** The one thing a viewer with Apple hardware needs to know about a profile 7 file, or nothing. A dual layer
 *  is the mark: no Apple device decodes it, so Apple TV and Infuse play the HDR10 base layer. */
export function dolbyVisionNote(detail: DolbyVisionDetail | null | undefined): string | null {
  if (!detail) {
    return null;
  }
  return detail.profile === 7 || detail.enhancementLayer ? "Apple TV and Infuse play its HDR10 base layer" : null;
}

/** What a re-encode does to this source's dynamic range, said before the operator commits to one. Profile-aware
 *  where the profile is known: profile 5 has no viewable base layer, 8.4 lands on HLG and 8.2 on SDR, while
 *  7 and 8.1 keep an HDR10 picture and lose only the dynamic layer. */
export function reencodeDynamicRangeWarning(hdrFormat: string | null | undefined, detail: DolbyVisionDetail | null | undefined): string | null {
  if (!hdrFormat) {
    return null;
  }
  if (!/dolby vision/i.test(hdrFormat)) {
    return `This source is ${hdrFormat}. Re-encoding won’t carry its HDR metadata — choose “Keep original video” to preserve it.`;
  }
  const label = dolbyVisionLabel(detail);
  if (detail?.profile === 5) {
    return `This source is ${label}. Its base layer is not viewable without the Dolby Vision layer, so a re-encode wrecks the colours — choose “Keep original video”.`;
  }
  if (detail?.profile === 8 && detail.blCompatibilityId === 4) {
    return `This source is ${label}. Re-encoding drops the Dolby Vision layer and lands on its HLG base layer — choose “Keep original video” to preserve it.`;
  }
  if (detail?.profile === 8 && detail.blCompatibilityId === 2) {
    return `This source is ${label}. Re-encoding drops the Dolby Vision layer and lands on its SDR base layer — choose “Keep original video” to preserve it.`;
  }
  return `This source is ${label}. Re-encoding drops the Dolby Vision (and any HDR10+) layer and keeps an HDR10 picture — choose “Keep original video” to preserve it.`;
}
