import { describe, expect, it } from "vitest";
import { buildAddTorrentTasks } from "./add-torrent";

const file = (name: string, base64: string) => ({ name, size: base64.length, base64 });

describe("buildAddTorrentTasks", () => {
  it("emits one task per file, all into the same catalog", () => {
    const tasks = buildAddTorrentTasks({
      catalogId: "cat-1",
      files: [file("a.torrent", "AAA"), file("b.torrent", "BBB")],
      keepSeeding: true,
    });

    expect(tasks).toEqual([
      { label: "a.torrent", input: { catalogId: "cat-1", torrentFileBase64: "AAA", keepSeeding: true } },
      { label: "b.torrent", input: { catalogId: "cat-1", torrentFileBase64: "BBB", keepSeeding: true } },
    ]);
  });

  it("adds the magnet as its own task, before the files", () => {
    const tasks = buildAddTorrentTasks({
      catalogId: "cat-1",
      magnet: "  magnet:?xt=urn:btih:abc  ",
      files: [file("a.torrent", "AAA")],
      keepSeeding: false,
    });

    expect(tasks).toEqual([
      { label: "magnet link", input: { catalogId: "cat-1", magnet: "magnet:?xt=urn:btih:abc", keepSeeding: false } },
      { label: "a.torrent", input: { catalogId: "cat-1", torrentFileBase64: "AAA", keepSeeding: false } },
    ]);
  });

  it("ignores a blank/whitespace-only magnet", () => {
    const tasks = buildAddTorrentTasks({ catalogId: "cat-1", magnet: "   ", files: [], keepSeeding: false });
    expect(tasks).toEqual([]);
  });

  it("returns nothing when there is no source", () => {
    expect(buildAddTorrentTasks({ catalogId: "cat-1", files: [], keepSeeding: false })).toEqual([]);
  });
});
