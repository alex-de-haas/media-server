import type { CatalogType } from "@/lib/media-server";

export type LibraryKind = "Movie" | "Series";

/** A query parameter can be repeated; catalog browsing accepts exactly one non-empty value. */
export function catalogSearchParam(value: string | string[] | undefined): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

/** Adds the catalog browsing context to a list or detail route. */
export function withCatalog(href: string, catalogId: string | undefined): string {
  return withParam(href, "catalog", catalogId);
}

/**
 * Whether the grid is also showing the user's removed titles. In the URL rather than in local storage,
 * like the catalog filter: the view survives a refresh, a back button, and being sent to someone else.
 */
export function removedSearchParam(value: string | string[] | undefined): boolean {
  return value === "1";
}

export function withRemoved(href: string, showRemoved: boolean): string {
  return withParam(href, "removed", showRemoved ? "1" : undefined);
}

function withParam(href: string, name: string, value: string | undefined): string {
  const hashIndex = href.indexOf("#");
  const hash = hashIndex >= 0 ? href.slice(hashIndex) : "";
  const pathAndSearch = hashIndex >= 0 ? href.slice(0, hashIndex) : href;
  const searchIndex = pathAndSearch.indexOf("?");
  const path = searchIndex >= 0 ? pathAndSearch.slice(0, searchIndex) : pathAndSearch;
  const params = new URLSearchParams(searchIndex >= 0 ? pathAndSearch.slice(searchIndex + 1) : "");

  if (value) {
    params.set(name, value);
  } else {
    params.delete(name);
  }

  const queryString = params.toString();
  return `${path}${queryString ? `?${queryString}` : ""}${hash}`;
}

/** Catalog types that can contribute top-level items to a media-kind page. */
export function catalogAppliesToKind(type: CatalogType, kind: LibraryKind): boolean {
  return kind === "Movie" ? type === "Movie" : type === "Series" || type === "Anime";
}

/** User-facing browse destination for an operator-configured catalog. */
export function catalogBrowseHref(catalog: { id: string; type: CatalogType }): string {
  return withCatalog(catalog.type === "Movie" ? "/movies" : "/series", catalog.id);
}
