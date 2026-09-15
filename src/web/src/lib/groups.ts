import { apiFetch, apiJson } from "@/lib/api";
import type { LibraryItem } from "@/lib/media-server";

export type GroupCatalogType = "movie" | "series" | "anime";
export type GroupKind = "manual" | "smart";
export interface GroupCondition {
  field: "year" | "resolution" | "hdr" | "tag" | "genre";
  operator: string;
  value: string;
  endValue?: string | null;
}
export interface GroupRules { version: number; match: "all" | "any"; conditions: GroupCondition[] }
export interface GroupInput { name: string; kind: GroupKind; catalogType: GroupCatalogType; rules: GroupRules; memberIds: string[] }
export interface GroupDefinition extends GroupInput { id: string }
export interface GroupSummary { id: string; name: string; kind: GroupKind; catalogType: GroupCatalogType; itemCount: number }
export interface GroupPage { items: LibraryItem[]; total: number; limit: number; offset: number }
export interface GroupDetail extends GroupPage { id: string; name: string; kind: GroupKind; catalogType: GroupCatalogType }
export interface GroupOptions { resolutions: string[]; hdrFormats: string[]; tags: string[]; genres: string[] }
const BASE = "/api/proxy/api/groups";
const body = (value: unknown) => ({ headers: { "content-type": "application/json" }, body: JSON.stringify(value) });
export const groupsApi = {
  list: () => apiJson<GroupSummary[]>(BASE),
  detail: (id: string, offset = 0) => apiJson<GroupDetail>(`${BASE}/${id}?limit=60&offset=${offset}`),
  definition: (id: string) => apiJson<GroupDefinition>(`${BASE}/${id}/definition`),
  save: (id: string | undefined, input: GroupInput) => apiJson<GroupDefinition>(id ? `${BASE}/${id}` : BASE, { method: id ? "PUT" : "POST", ...body(input) }),
  remove: (id: string) => apiFetch(`${BASE}/${id}`, { method: "DELETE" }),
  preview: (input: GroupInput) => apiJson<GroupPage>(`${BASE}/preview?limit=12`, { method: "POST", ...body(input) }),
  candidates: (catalogType: GroupCatalogType, title: string, offset: number) => {
    const params = new URLSearchParams({ catalogType, offset: String(offset), limit: "30" });
    if (title.trim()) params.set("title", title.trim());
    return apiJson<GroupPage>(`${BASE}/candidates?${params}`);
  },
  options: (catalogType: GroupCatalogType, search: string) => apiJson<GroupOptions>(`${BASE}/options?${new URLSearchParams({ catalogType, search })}`),
};
export const groupTypeLabel = (type: GroupCatalogType) => ({ movie: "Movies", series: "Series", anime: "Anime" })[type];
export const hdrLabel = (value: string) => value === "any" ? "Any HDR" : value === "HDR" ? "HDR (unspecified type)" : value;
