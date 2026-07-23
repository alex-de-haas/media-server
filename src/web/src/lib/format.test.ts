import { describe, expect, it } from "vitest";
import { episodeLabel } from "@/lib/format";

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
