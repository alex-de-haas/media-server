import { describe, expect, it } from "vitest";
import { dolbyVisionLabel, dolbyVisionNote, dynamicRangeBadges, episodeLabel, objectAudioFormat, pictureStream, reencodeDynamicRangeWarning } from "@/lib/format";
import type { MediaStream } from "@/lib/media-server";

describe("objectAudioFormat", () => {
  it("reads the object layer out of the codec profile", () => {
    // ffprobe reports `truehd` as the codec either way; the profile is the only place this appears.
    expect(objectAudioFormat("Dolby TrueHD + Dolby Atmos")).toBe("Atmos");
    expect(objectAudioFormat("Dolby Digital Plus + Dolby Atmos")).toBe("Atmos");
    expect(objectAudioFormat("DTS-HD MA + DTS:X")).toBe("DTS:X");
  });

  it("says nothing for a profile that carries no object layer", () => {
    expect(objectAudioFormat("DTS-HD MA")).toBeNull();
    expect(objectAudioFormat("Dolby TrueHD")).toBeNull();
    expect(objectAudioFormat("DTS")).toBeNull();
  });

  it("treats a missing profile as unknown rather than as absence", () => {
    // A file probed from container headers carries no profile at all — that is "nobody looked", and the
    // caller shows nothing rather than implying the track has no Atmos.
    expect(objectAudioFormat(null)).toBeNull();
    expect(objectAudioFormat(undefined)).toBeNull();
    expect(objectAudioFormat("")).toBeNull();
  });
});

describe("episodeLabel", () => {
  it("zero-pads a single episode", () => {
    expect(episodeLabel(1, 1)).toBe("S01E01");
    expect(episodeLabel(1, 1, null)).toBe("S01E01");
    expect(episodeLabel(12, 13)).toBe("S12E13");
  });

  it("renders a range when one file covers consecutive episodes", () => {
    // The Warehouse 13 case: `S01E01E02` on disk is one item numbered 1 with the end at 2, and no item
    // for episode 2 — without the range the season reads "1, 3, 4…" and episode 2 looks lost.
    expect(episodeLabel(1, 1, 2)).toBe("S01E01-E02");
    expect(episodeLabel(2, 9, 10)).toBe("S02E09-E10");
  });

  it("ignores an end that does not extend the range", () => {
    expect(episodeLabel(1, 3, 3)).toBe("S01E03");
    expect(episodeLabel(1, 3, 2)).toBe("S01E03");
  });

  it("falls back to zero for a missing season or episode number", () => {
    expect(episodeLabel(null, null)).toBe("S00E00");
    expect(episodeLabel(undefined, 4)).toBe("S00E04");
  });
});

const profile7 = { profile: 7, level: 6, blCompatibilityId: 6, enhancementLayer: true };
const profile81 = { profile: 8, level: 6, blCompatibilityId: 1, enhancementLayer: false };
const profile84 = { profile: 8, level: 6, blCompatibilityId: 4, enhancementLayer: false };
const profile5 = { profile: 5, level: 6, blCompatibilityId: 0, enhancementLayer: false };

describe("dolbyVisionLabel", () => {
  it("names profile 8 by its base layer and the others by profile alone", () => {
    expect(dolbyVisionLabel(profile81)).toBe("Dolby Vision 8.1");
    expect(dolbyVisionLabel(profile84)).toBe("Dolby Vision 8.4");
    expect(dolbyVisionLabel(profile7)).toBe("Dolby Vision 7");
    expect(dolbyVisionLabel(profile5)).toBe("Dolby Vision 5");
  });

  it("stays the bare name while the profile is not recorded", () => {
    expect(dolbyVisionLabel(null)).toBe("Dolby Vision");
  });
});

describe("dynamicRangeBadges", () => {
  it("makes one badge per format the probe named", () => {
    // Production holds "Dolby Vision · HDR10" — what a profile 8.1 file honestly is.
    expect(dynamicRangeBadges("Dolby Vision · HDR10", profile81)).toEqual(["Dolby Vision 8.1", "HDR10"]);
    expect(dynamicRangeBadges("Dolby Vision", profile7)).toEqual(["Dolby Vision 7"]);
    expect(dynamicRangeBadges("HDR10+", null)).toEqual(["HDR10+"]);
    expect(dynamicRangeBadges("HDR", null)).toEqual(["HDR"]);
  });

  it("shows nothing for SDR or an unknown range", () => {
    expect(dynamicRangeBadges("SDR", null)).toEqual([]);
    expect(dynamicRangeBadges(null, null)).toEqual([]);
  });

  it("keeps the bare name while the profile is not yet recorded", () => {
    expect(dynamicRangeBadges("Dolby Vision", null)).toEqual(["Dolby Vision"]);
  });
});

describe("dolbyVisionNote", () => {
  it("warns about a dual layer and nothing else", () => {
    expect(dolbyVisionNote(profile7)).toBe("Apple TV and Infuse play its HDR10 base layer");
    expect(dolbyVisionNote(profile81)).toBeNull();
    expect(dolbyVisionNote(profile5)).toBeNull();
    expect(dolbyVisionNote(null)).toBeNull();
  });
});

describe("reencodeDynamicRangeWarning", () => {
  it("says what a re-encode leaves behind, per profile", () => {
    expect(reencodeDynamicRangeWarning("Dolby Vision", profile7)).toContain("keeps an HDR10 picture");
    expect(reencodeDynamicRangeWarning("Dolby Vision", profile81)).toContain("keeps an HDR10 picture");
    expect(reencodeDynamicRangeWarning("Dolby Vision", profile5)).toContain("wrecks the colours");
    expect(reencodeDynamicRangeWarning("Dolby Vision", profile84)).toContain("HLG base layer");
    expect(reencodeDynamicRangeWarning("Dolby Vision", { ...profile81, blCompatibilityId: 2 })).toContain("SDR base layer");
  });

  it("falls back to the generic warnings without a record", () => {
    expect(reencodeDynamicRangeWarning("Dolby Vision", null)).toContain("This source is Dolby Vision.");
    expect(reencodeDynamicRangeWarning("HDR10", null)).toContain("won’t carry its HDR metadata");
    expect(reencodeDynamicRangeWarning(null, null)).toBeNull();
  });
});

describe("pictureStream", () => {
  const stream = (over: Partial<MediaStream>): MediaStream => ({
    id: over.id ?? "s", type: "Video", index: 0, codec: null, language: null, displayTitle: null, title: null,
    width: null, height: null, hdrFormat: null, dolbyVision: null, channels: null, profile: null, frameRate: null,
    bitDepth: null, sampleRate: null, bitrate: null, isDefault: false, isForced: false, isExternal: false, fileName: null,
    ...over,
  });

  it("passes over cover art a muxer wrote as a video track", () => {
    // The cover sorts first and can carry an SDR range of its own; judging it would hide the film's Dolby Vision.
    const cover = stream({ id: "cover", index: 0, codec: "mjpeg", hdrFormat: "SDR" });
    const film = stream({ id: "film", index: 1, codec: "hevc", hdrFormat: "Dolby Vision" });
    expect(pictureStream([cover, film])?.id).toBe("film");
    expect(pictureStream([film, cover])?.id).toBe("film");
  });

  it("falls back to the first video when every video is a still, and to nothing without one", () => {
    const cover = stream({ id: "cover", codec: "png" });
    expect(pictureStream([cover])?.id).toBe("cover");
    expect(pictureStream([stream({ type: "Audio", codec: "eac3" })])).toBeNull();
  });

  it("leaves sidecars out: a picture is inside the file", () => {
    const external = stream({ id: "ext", codec: "hevc", isExternal: true });
    const film = stream({ id: "film", index: 1, codec: "hevc" });
    expect(pictureStream([external, film])?.id).toBe("film");
  });
});
