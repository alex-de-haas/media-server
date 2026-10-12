import { describe, expect, it } from "vitest";
import { managementHref, managementQuery } from "./management";
describe("management navigation", () => {
  it("keeps source filters but strips presentation state at the API boundary", () => {
    const query = new URLSearchParams(managementQuery(new URLSearchParams("view=dolby-vision&resolution=2160p,1080p&expanded=m1&hidden=size&source=v1&offset=50"), "Movie"));
    expect(query.get("resolution")).toBe("2160p,1080p");
    expect(query.get("offset")).toBe("50");
    expect(query.has("expanded")).toBe(false); expect(query.has("hidden")).toBe(false); expect(query.has("source")).toBe(false);
  });
  it("targets an episode within its series without inheriting library filters", () => {
    expect(managementHref("Episode", "e1", "s1")).toBe("/manage/series/s1?episode=e1");
    const q = new URLSearchParams(managementQuery(new URLSearchParams(), "Episode", "e1", "s1"));
    expect(q.get("itemId")).toBe("e1"); expect(q.get("seriesId")).toBe("s1"); expect(q.get("sort")).toBe("episode");
  });
  it("locates a movie in the shared list without filtering out its neighbours", () => {
    expect(managementHref("Movie", "m1")).toBe("/manage/movies?focus=m1&expanded=m1");
    const href = managementHref("Movie", "m1", undefined, { removed: "1" });
    const search = new URLSearchParams(href.split("?")[1]);
    expect(search.get("removed")).toBe("1");
    expect(search.get("focus")).toBe("m1");
    const query = new URLSearchParams(managementQuery(search, "Movie"));
    expect(query.get("focusId")).toBe("m1");
    expect(query.has("itemId")).toBe(false);
    expect(query.has("removed")).toBe(false);
  });
});
