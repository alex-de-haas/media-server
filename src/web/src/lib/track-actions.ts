import { isBluraySource, type CreateTranscodeInput, type LibraryMediaSource, type MediaStream } from "./media-server";

export type TrackSelection = { sourceId: string; streamIds: string[] };
export type TrackOperation = "remux" | "convert";

/** Embedded tracks belong to one container; separate files are usable by every version of this title. */
export function trackSelectionSource(sourceId: string | undefined, sources: LibraryMediaSource[]): LibraryMediaSource | undefined {
  const source = sources.find(source => source.id === sourceId);
  if (!source || isBluraySource(source)) return undefined;
  return { ...source, streams: [
    ...source.streams.filter(stream => !stream.isExternal),
    ...sources.flatMap(source => source.streams.filter(stream => stream.isExternal)),
  ] };
}

export function cleanTrackSelection(selection: TrackSelection | null, sources: LibraryMediaSource[]): TrackSelection | null {
  const source = trackSelectionSource(selection?.sourceId, sources);
  if (!source) return null;
  const streamIds = selection!.streamIds.filter(id => source.streams.some(stream => stream.id === id));
  return streamIds.length ? { sourceId: source.id, streamIds } : null;
}

export function toggleTrackSelection(selection: TrackSelection | null, source: LibraryMediaSource, stream: MediaStream, checked: boolean, sources: LibraryMediaSource[]): TrackSelection | null {
  const cleaned = cleanTrackSelection(selection, sources);
  const selectedSource = trackSelectionSource(cleaned?.sourceId, sources);
  const hasEmbedded = selectedSource?.streams.some(track => !track.isExternal && cleaned?.streamIds.includes(track.id));
  if (!stream.isExternal && hasEmbedded && cleaned?.sourceId !== source.id) return cleaned;
  const streamIds = checked ? [...new Set([...(cleaned?.streamIds ?? []), stream.id])] : (cleaned?.streamIds ?? []).filter(id => id !== stream.id);
  const sourceId = stream.isExternal ? cleaned?.sourceId ?? source.id : source.id;
  return cleanTrackSelection({ sourceId, streamIds }, sources);
}

export function extractionReason(stream: MediaStream): string | null {
  if (stream.isExternal) return "This track is already a separate file.";
  if (stream.type === "Audio") return null;
  if (stream.type !== "Subtitle") return "Only audio and text subtitles can be extracted.";
  return ["subrip", "srt", "mov_text", "tx3g", "ass", "ssa", "webvtt", "vtt"].includes(stream.codec?.toLowerCase() ?? "")
    ? null : "This subtitle format cannot be extracted as a text file.";
}

export function compositionReason(streams: MediaStream[]): string | null {
  if (streams.some(stream => !["Video", "Audio", "Subtitle"].includes(stream.type) || (stream.isExternal && stream.type === "Video")))
    return "Select tracks from one container and separate audio or subtitle files from this title.";
  if (streams.filter(stream => stream.type === "Video" && !stream.isExternal).length !== 1)
    return "Select exactly one video track.";
  if (!streams.some(stream => stream.type === "Audio")) return "Select at least one audio track.";
  return null;
}

export const trackLabel = (stream: MediaStream) => stream.isExternal ? stream.fileName ?? stream.type : `${stream.type === "Subtitle" ? "Subtitle" : stream.type} track ${stream.index + 1}`;
export const audioBitrateFor = (channels: number | null) => (channels ?? 2) > 2 ? 640 : channels === 1 ? 128 : 256;

export interface TrackJobSettings {
  versionName: string;
  operation: TrackOperation;
  titles: Record<string, string>;
  codec: string;
  hardware: string;
  quality: string;
  resolution: string;
  dolbyVision: boolean;
  reencoded: string[];
}

export function trackJobInput(source: LibraryMediaSource, selected: MediaStream[], settings: TrackJobSettings): CreateTranscodeInput {
  const reason = compositionReason(selected);
  if (reason || selected.some(stream => !source.streams.includes(stream))) throw new Error(reason ?? "The selected tracks have changed. Select them again.");
  const copy = settings.operation === "remux";
  const video = selected.find(stream => stream.type === "Video")!;
  return {
    versionName: settings.versionName.trim(),
    sourceId: source.id,
    videoStreamIndex: video.index,
    videoCodec: copy ? "copy" : settings.codec,
    hardwareAcceleration: copy ? undefined : settings.hardware,
    qualityLevel: copy ? null : settings.quality,
    maxHeight: copy || settings.resolution === "source" ? null : Number(settings.resolution),
    audioStreamIndexes: selected.filter(stream => !stream.isExternal && stream.type === "Audio").map(stream => stream.index),
    subtitleStreamIndexes: selected.filter(stream => !stream.isExternal && stream.type === "Subtitle").map(stream => stream.index),
    mergeStreamIds: selected.filter(stream => stream.isExternal).map(stream => stream.id),
    metadataEdits: selected.flatMap(stream => {
      const title = settings.titles[stream.id]?.trim();
      return title !== undefined && title !== (stream.title ?? "") ? [{ streamId: stream.id, title }] : [];
    }),
    dolbyVision: copy && settings.dolbyVision && video.dolbyVision?.profile === 7 ? "toProfile81" : undefined,
    audioTargets: selected.filter(stream => !stream.isExternal && stream.type === "Audio" && settings.reencoded.includes(stream.id))
      .map(stream => ({ streamId: stream.id, codec: "eac3", bitrate: audioBitrateFor(stream.channels) })),
  };
}

export function versionNameError(value: string): string | null {
  const name = value.trim();
  if (!name) return "Enter a version name.";
  if (name.length > 120) return "Use no more than 120 characters for the version name.";
  if (/[/\\:*?"<>|]/.test(name) || [...name].some(character => character.charCodeAt(0) < 32 || (character.charCodeAt(0) >= 127 && character.charCodeAt(0) <= 159)))
    return "The version name can't contain filename separators or control characters.";
  if (!/[\p{L}\p{N}]/u.test(name)) return "The version name must contain a letter or number.";
  return null;
}

/** Estimates only the selected tracks, never substitutes a container bitrate for missing track facts. */
export function selectedOutputEstimate(source: LibraryMediaSource, selected: MediaStream[], settings: TrackJobSettings): number | null {
  const duration = source.durationTicks / 10_000_000;
  if (duration <= 0) return null;
  const shares: Record<string, number> = { highest: .44, high: .3, balanced: .18, small: .1 };
  let total = 0;
  for (const stream of selected) {
    const audioEncode = !stream.isExternal && stream.type === "Audio" && settings.reencoded.includes(stream.id);
    let bitrate = audioEncode ? audioBitrateFor(stream.channels) * 1000 : stream.bitrate;
    if (!bitrate) return null;
    if (settings.operation === "convert" && stream.type === "Video") {
      const scale = settings.resolution !== "source" && stream.height ? Math.min(1, Number(settings.resolution) / stream.height) ** 2 : 1;
      bitrate *= (shares[settings.quality] ?? .3) * scale;
    }
    total += duration * bitrate / 8;
  }
  return total;
}
