import { describe, expect, it } from "vitest";
import {
  catalogAppliesToKind,
  catalogBrowseHref,
  catalogSearchParam,
  withCatalog,
} from "@/lib/catalog-navigation";

describe("catalog navigation", () => {
  it("accepts one non-empty catalog query value", () => {
    expect(catalogSearchParam("catalog-id")).toBe("catalog-id");
    expect(catalogSearchParam("")).toBeUndefined();
    expect(catalogSearchParam(["one", "two"])).toBeUndefined();
  });

  it("preserves a catalog on list and detail routes", () => {
    expect(withCatalog("/movies", "movies 4k")).toBe("/movies?catalog=movies+4k");
    expect(withCatalog("/movies/m1", undefined)).toBe("/movies/m1");
  });

  it("replaces or removes catalog context without dropping other URL state", () => {
    expect(withCatalog("/movies?search=arrival&catalog=old&page=2", "new")).toBe(
      "/movies?search=arrival&catalog=new&page=2",
    );
    expect(withCatalog("/movies?search=arrival&catalog=old#results", undefined)).toBe(
      "/movies?search=arrival#results",
    );
  });

  it("maps movie catalogs to Movies and episodic catalogs to Series", () => {
    expect(catalogBrowseHref({ id: "m", type: "Movie" })).toBe("/movies?catalog=m");
    expect(catalogBrowseHref({ id: "s", type: "Series" })).toBe("/series?catalog=s");
    expect(catalogBrowseHref({ id: "a", type: "Anime" })).toBe("/series?catalog=a");
  });

  it("offers only catalogs applicable to the media-kind page", () => {
    expect(catalogAppliesToKind("Movie", "Movie")).toBe(true);
    expect(catalogAppliesToKind("Anime", "Movie")).toBe(false);
    expect(catalogAppliesToKind("Series", "Series")).toBe(true);
    expect(catalogAppliesToKind("Anime", "Series")).toBe(true);
  });
});
