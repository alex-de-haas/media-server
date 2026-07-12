import type { CatalogType } from "@/lib/media-server";

export type LibraryKind = "Movie" | "Series";

/** A query parameter can be repeated; catalog browsing accepts exactly one non-empty value. */
export function catalogSearchParam(value: string | string[] | undefined): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

/** Adds the catalog browsing context to a list or detail route. */
export function withCatalog(href: string, catalogId: string | undefined): string {
  if (!catalogId) {
    return href;
  }

  const separator = href.includes("?") ? "&" : "?";
  return `${href}${separator}catalog=${encodeURIComponent(catalogId)}`;
}

/** Catalog types that can contribute top-level items to a media-kind page. */
export function catalogAppliesToKind(type: CatalogType, kind: LibraryKind): boolean {
  return kind === "Movie" ? type === "Movie" : type === "Series" || type === "Anime";
}

/** User-facing browse destination for an operator-configured catalog. */
export function catalogBrowseHref(catalog: { id: string; type: CatalogType }): string {
  return withCatalog(catalog.type === "Movie" ? "/movies" : "/series", catalog.id);
}
