import { describe, expect, it } from "vitest";
import type { BlurayPlaylist } from "./media-server";
import { describePlaylists, playlistDuration } from "./bluray-playlists";

const playlist = (id: string, durationSeconds: number, error: string | null = null): BlurayPlaylist =>
  ({ id, durationSeconds, error, chapters: 1, clips: [], tracks: [] });

describe("playlist guidance", () => {
  it("puts usable long titles first without recommending an unreadable title", () => {
    const input = [playlist("extra", 30), playlist("broken", 9000, "Unreadable"), playlist("film", 7200)];
    const result = describePlaylists(input);
    expect(result.sorted.map(p => p.id)).toEqual(["film", "extra", "broken"]);
    expect([...result.candidateIds]).toEqual(["film"]);
    expect(result.ambiguous).toBe(false);
    expect(input[0].id).toBe("extra");
  });

  it("keeps alternate cuts and equal-length titles ambiguous", () => {
    const result = describePlaylists([playlist("00003", 7200), playlist("00001", 7200), playlist("00002", 6800), playlist("extra", 90)]);
    expect([...result.candidateIds]).toEqual(["00001", "00003", "00002"]);
    expect(result.ambiguous).toBe(true);
  });

  it("does not recommend a zero-duration or unavailable playlist", () => {
    expect(describePlaylists([playlist("empty", 0), playlist("broken", 7200, "Unreadable")]).candidateIds.size).toBe(0);
    expect(describePlaylists([]).ambiguous).toBe(false);
  });

  it("prefers matches to the movie runtime over a longer title and preserves ambiguity", () => {
    const result = describePlaylists([playlist("00001", 6908), playlist("00005", 4612), playlist("00013", 4612)], 4440);
    expect(result.sorted.map(p => p.id)).toEqual(["00005", "00013", "00001"]);
    expect([...result.candidateIds]).toEqual(["00005", "00013"]);
    expect(result.ambiguous).toBe(true);
    expect(result.matchedRuntime).toBe(true);
  });

  it("falls back to the longest titles when metadata does not match any playlist", () => {
    const result = describePlaylists([playlist("film", 7200), playlist("extra", 30)], 3600);
    expect([...result.candidateIds]).toEqual(["film"]);
    expect(result.matchedRuntime).toBe(false);
  });

  it("shows seconds so short extras and close running times remain distinguishable", () => {
    expect(playlistDuration(30.9)).toBe("0m 30s");
    expect(playlistDuration(7231)).toBe("2h 0m 31s");
  });
});
