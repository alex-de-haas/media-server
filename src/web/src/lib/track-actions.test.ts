import { describe, expect, it } from "vitest";
import { cleanTrackSelection, compositionReason, extractionReason, selectedOutputEstimate, toggleTrackSelection, trackSelectionSource, trackJobInput, versionNameError, type TrackJobSettings } from "./track-actions";
import type { LibraryMediaSource, MediaStream } from "./media-server";
const stream = (id: string, type: string, index: number, extra: Partial<MediaStream> = {}) => ({ id, type, index, isExternal: false, title: null, bitrate: 1000, codec: "aac", ...extra }) as MediaStream;
const video = stream("v", "Video", 3, { codec: "hevc", height: 1080 });
const audio = stream("a", "Audio", 1);
const sidecar = stream("x", "Audio", 1000, { isExternal: true });
const sub = stream("s", "Subtitle", 5, { codec: "subrip" });
const source = { id: "source", container: "mkv", streams: [video, audio, sidecar, sub], durationTicks: 10_000_000 } as LibraryMediaSource;
const settings: TrackJobSettings = { operation: "remux", versionName: "Remux", titles: {}, codec: "hevc", hardware: "auto", quality: "high", resolution: "source", reencoded: [], dolbyVision: false };
describe("grid track actions", () => {
  it("validates a version name and submits it independently of codec and track titles", () => {
    for (const name of ["", "   ", "../escape", "folder\\name", "bad:name", "...", "a\u0000b", "x".repeat(121)]) expect(versionNameError(name)).not.toBeNull();
    expect(versionNameError("  Кино — Director’s Cut  ")).toBeNull();
    expect(trackJobInput(source, [video, audio], { ...settings, versionName: "  My cut  " }).versionName).toBe("My cut");
  });
  it("requires one embedded video and audio, accepts sidecars and optional subtitles", () => {
    expect(compositionReason([video])).toMatch(/audio/);
    expect(compositionReason([audio])).toMatch(/video/);
    expect(compositionReason([video, video, audio])).toMatch(/exactly one/);
    expect(compositionReason([video, sidecar, sub])).toBeNull();
    expect(compositionReason([video, audio, stream("data", "Data", 6)])).not.toBeNull();
  });
  it("cleans removed sources and stale track ids without crossing containers", () => {
    expect(cleanTrackSelection({ sourceId: "gone", streamIds: ["a"] }, [source])).toBeNull();
    expect(cleanTrackSelection({ sourceId: "source", streamIds: ["gone", "a"] }, [source])).toEqual({ sourceId: "source", streamIds: ["a"] });
  });
  it("extracts only embedded audio and supported text subtitles", () => {
    expect(extractionReason(audio)).toBeNull(); expect(extractionReason(sub)).toBeNull();
    for (const item of [video, sidecar, { ...sub, codec: "hdmv_pgs_subtitle" }, { ...sub, codec: null }]) expect(extractionReason(item)).not.toBeNull();
  });
  it.each(["remux", "convert"] as const)("%s includes external audio and subtitles attached to another version", operation => {
    const externalSub = stream("xs", "Subtitle", 1001, { isExternal: true, codec: "subrip" });
    const original = { ...source, streams: [...source.streams, externalSub] };
    const copy = { ...source, id: "copy", streams: [stream("cv", "Video", 0), stream("ca", "Audio", 1)] };
    const sources = [copy, original];
    let selection = toggleTrackSelection(null, original, sidecar, true, sources);
    selection = toggleTrackSelection(selection, copy, copy.streams[0], true, sources);
    selection = toggleTrackSelection(selection, copy, copy.streams[1], true, sources);
    selection = toggleTrackSelection(selection, original, externalSub, true, sources);
    expect(selection).toEqual({ sourceId: "copy", streamIds: ["x", "cv", "ca", "xs"] });
    expect(toggleTrackSelection(selection, original, video, true, sources)).toEqual(selection);
    const selectedSource = trackSelectionSource(selection!.sourceId, sources)!;
    const selected = selectedSource.streams.filter(stream => selection!.streamIds.includes(stream.id));
    expect(trackJobInput(selectedSource, selected, { ...settings, operation, titles: { x: "Dub", xs: "Translated" } })).toMatchObject({
      sourceId: "copy", videoCodec: operation === "remux" ? "copy" : "hevc", videoStreamIndex: 0,
      audioStreamIndexes: [1], subtitleStreamIndexes: [], mergeStreamIds: ["x", "xs"],
      metadataEdits: [{ streamId: "x", title: "Dub" }, { streamId: "xs", title: "Translated" }],
    });
    expect(cleanTrackSelection(selection, [copy])).toEqual({ sourceId: "copy", streamIds: ["cv", "ca"] });
    const withoutVideo = toggleTrackSelection(selection, copy, copy.streams[0], false, sources);
    const externalOnly = toggleTrackSelection(withoutVideo, copy, copy.streams[1], false, sources);
    expect(toggleTrackSelection(externalOnly, original, video, true, sources)).toEqual({ sourceId: "source", streamIds: ["x", "xs", "v"] });
  });
  it("sends exact selections and only changed names including video and sidecar", () => {
    const body = trackJobInput(source, [video, sidecar], { ...settings, titles: { v: "Picture", x: "Dub", s: "Excluded" } });
    expect(body).toMatchObject({ videoCodec: "copy", videoStreamIndex: 3, audioStreamIndexes: [], subtitleStreamIndexes: [], mergeStreamIds: ["x"], metadataEdits: [{ streamId: "v", title: "Picture" }, { streamId: "x", title: "Dub" }] });
    expect(body.defaultAudioStreamIndex).toBeUndefined();
    expect(() => trackJobInput(source, [video, { ...audio, id: "foreign" }], settings)).toThrow(/changed/);
  });
  it("convert always encodes the selected video and only selected embedded audio", () => {
    const body = trackJobInput(source, [video, audio, sidecar], { ...settings, operation: "convert", reencoded: ["a", "x"], resolution: "720", quality: "small", dolbyVision: true });
    expect(body).toMatchObject({ videoCodec: "hevc", videoStreamIndex: 3, maxHeight: 720, qualityLevel: "small", audioTargets: [{ streamId: "a", codec: "eac3", bitrate: 256 }] });
    expect(body.dolbyVision).toBeUndefined();
  });
  it.each([1, 2, 6, 8, null])("remux can re-encode %s-channel audio while copying video and other tracks", channels => {
    const dub = stream("dub", "Audio", 6, { channels, codec: "dts" });
    const dvVideo = { ...video, dolbyVision: { profile: 7 } } as MediaStream;
    const withDub = { ...source, streams: [dvVideo, audio, dub, sidecar, sub] };
    const body = trackJobInput(withDub, withDub.streams, {
      ...settings, reencoded: ["dub", "x", "v", "s", "excluded"], dolbyVision: true,
      resolution: "720", quality: "small", titles: { dub: "Dub" },
    });
    expect(body).toMatchObject({ videoCodec: "copy", videoStreamIndex: 3, maxHeight: null, qualityLevel: null,
      audioStreamIndexes: [1, 6], subtitleStreamIndexes: [5], mergeStreamIds: ["x"],
      dolbyVision: "toProfile81", metadataEdits: [{ streamId: "dub", title: "Dub" }],
      audioTargets: [{ streamId: "dub", codec: "eac3", bitrate: channels === 1 ? 128 : channels === null || channels === 2 ? 256 : 640 }] });
    expect(body.hardwareAcceleration).toBeUndefined();
    expect(trackJobInput(withDub, [dvVideo, audio], { ...settings, reencoded: ["dub"] }).audioTargets).toEqual([]);
    expect(trackJobInput(withDub, withDub.streams, settings).audioTargets).toEqual([]);
  });
  it.each(["remux", "convert"] as const)("%s estimates encoded audio even when its source bitrate is unknown", operation => {
    const unknownAudio = { ...audio, bitrate: null, channels: 6 };
    const selected = [video, unknownAudio, sidecar];
    const encoding = { ...settings, operation, reencoded: ["a", "x"] };
    expect(selectedOutputEstimate(source, selected, encoding)).toBe(operation === "remux" ? 80250 : 80162.5);
    expect(selectedOutputEstimate(source, selected, { ...encoding, reencoded: [] })).toBeNull();
    expect(selectedOutputEstimate(source, [video, unknownAudio, { ...sidecar, bitrate: null }], encoding)).toBeNull();
  });
  it("estimates the selected video and preserves unknown bitrates", () => {
    expect(selectedOutputEstimate(source, [video, audio], settings)).toBe(250);
    expect(selectedOutputEstimate(source, [video, audio], { ...settings, operation: "convert" })).toBe(162.5);
    expect(selectedOutputEstimate(source, [video, { ...audio, bitrate: null }], settings)).toBeNull();
  });
});
