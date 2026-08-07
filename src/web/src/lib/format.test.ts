import { describe, expect, it } from "vitest";
import { episodeLabel, objectAudioFormat } from "@/lib/format";

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
