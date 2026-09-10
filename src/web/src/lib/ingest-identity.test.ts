import { describe, expect, it } from "vitest";
import { ingestMatchLabel, ingestTitle } from "@/lib/ingest-identity";
import type { IngestItem, IngestSourceFile } from "@/lib/media-server";

function file(overrides: Partial<IngestSourceFile> = {}): IngestSourceFile {
  return {
    id: "f1", relativePath: "1 фильм.mkv", sizeBytes: 1024,
    assignmentStatus: "Confirmed", mediaItemId: "m1", companionKind: null,
    assigned: {
      kind: "Movie", title: "Остров сокровищ", season: null, episode: null,
      seriesTitle: null, provider: "tmdb", providerId: "1",
    },
    parsedTitle: "1 фильм", parsedYear: null, parsedSeason: null, parsedEpisode: null,
    extraKind: null, extraTitle: null, extraSuggestSkip: false,
    ...overrides,
  };
}

function item(sourceFiles: IngestSourceFile[], overrides: Partial<IngestItem> = {}): IngestItem {
  return {
    id: "i1", catalogId: "c1", downloadId: "d1", downloadName: "Original torrent name",
    mediaTitle: null, mediaItemId: null, targetProvider: null, targetProviderId: null,
    targetKind: null, targetTitle: null, targetYear: null,
    stage: "Download", status: "Pending", attemptCount: 0, stagesCompleted: ["intake"],
    lastError: null, nextAttemptAt: null, conflictCatalogId: null, canRetarget: false,
    reviewCandidates: [], sourceFiles, createdAt: "2026-09-10T10:00:00Z", updatedAt: "2026-09-10T10:00:00Z",
    ...overrides,
  };
}

describe("Activity identity", () => {
  it("shows a pre-matched title and marker for two files of the same movie", () => {
    const pack = item([file(), file({ id: "f2", relativePath: "2 фильм.mkv" })]);
    expect(ingestTitle(pack)).toBe("Остров сокровищ");
    expect(ingestMatchLabel(pack)).toBe("Matched 2 of 2 video files");
  });

  it("counts distinct movies by identity even when their titles are identical", () => {
    const pack = item([file(), file({ id: "f2", mediaItemId: "m2" })]);
    expect(ingestTitle(pack)).toBe("Остров сокровищ (+1 more)");
  });

  it("shows a partial count and prefers the chosen movie over an earlier guess or pin", () => {
    const pack = item([
      file({ assignmentStatus: "Unassigned", mediaItemId: null, assigned: null }),
      file({ id: "f2" }),
    ], { targetTitle: "Old pin", mediaTitle: "Old guess" });
    expect(ingestTitle(pack)).toBe("Остров сокровищ");
    expect(ingestMatchLabel(pack)).toBe("Matched 1 of 2 video files");
  });

  it("ignores skipped videos and companion tracks in the title and match count", () => {
    const pack = item([
      file(),
      file({ id: "f2", assignmentStatus: "Skipped", mediaItemId: null, assigned: null }),
      file({ id: "a1", companionKind: "Audio", mediaItemId: "m2" }),
      file({ id: "s1", companionKind: "Subtitle", assignmentStatus: "Unassigned", assigned: null }),
    ]);
    expect(ingestTitle(pack)).toBe("Остров сокровищ");
    expect(ingestMatchLabel(pack)).toBe("Matched 1 of 1 video files");
  });

  it("keeps the final primary title and pack count after Identify, without the early-match marker", () => {
    const pack = item([file(), file({ id: "f2", mediaItemId: "m2" })], {
      mediaTitle: "Enriched title", stagesCompleted: ["intake", "download", "identify"],
    });
    expect(ingestTitle(pack)).toBe("Enriched title (+1 more)");
    expect(ingestMatchLabel(pack)).toBeNull();
    expect(ingestMatchLabel(item([file()], { status: "Done" }))).toBeNull();
  });

  it("keeps season packs under their series title", () => {
    const episode = file({ assigned: { ...file().assigned!, kind: "Episode", title: "Pilot", seriesTitle: "Series" } });
    const pack = item([episode, { ...episode, id: "f2", mediaItemId: "m2" }], { mediaTitle: "Series" });
    expect(ingestTitle(pack)).toBe("Series");
    expect(ingestMatchLabel(pack)).toBeNull();
  });

  it("retains pin, parsed title, and torrent name fallbacks for unmapped downloads", () => {
    const pack = item([file({ assignmentStatus: "Unassigned", mediaItemId: null, assigned: null })]);
    expect(ingestTitle({ ...pack, targetTitle: "Pinned title" })).toBe("Pinned title");
    expect(ingestTitle(pack)).toBe("1 фильм");
    expect(ingestMatchLabel(pack)).toBeNull();
    expect(ingestTitle(item([]))).toBe("Original torrent name");
  });
});
