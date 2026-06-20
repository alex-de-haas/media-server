import { describe, expect, it } from "vitest";
import { infuseDeepLink } from "./infuse";

describe("infuseDeepLink", () => {
  it("builds a movie link, optionally auto-playing", () => {
    expect(infuseDeepLink({ kind: "movie", tmdbId: "27205" })).toBe("infuse://movie/27205");
    expect(infuseDeepLink({ kind: "movie", tmdbId: "27205" }, { play: true })).toBe("infuse://movie/27205?play");
  });

  it("builds a series link", () => {
    expect(infuseDeepLink({ kind: "series", tmdbId: "1396" })).toBe("infuse://series/1396");
  });

  it("builds an episode link from series id + season + episode", () => {
    expect(infuseDeepLink({ kind: "episode", seriesTmdbId: "1396", season: 2, episode: 5 }, { play: true })).toBe(
      "infuse://series/1396-2-5?play",
    );
  });

  it("returns null when the ids needed are missing", () => {
    expect(infuseDeepLink({ kind: "movie", tmdbId: null })).toBeNull();
    expect(infuseDeepLink({ kind: "series", tmdbId: null })).toBeNull();
    expect(infuseDeepLink({ kind: "episode", seriesTmdbId: null, season: 1, episode: 1 })).toBeNull();
    expect(infuseDeepLink({ kind: "episode", seriesTmdbId: "1396", season: null, episode: 1 })).toBeNull();
  });
});
