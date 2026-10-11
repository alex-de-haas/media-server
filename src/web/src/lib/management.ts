import { apiJson } from "@/lib/api";

export type Availability = "available" | "blocked" | "pending" | "unknown";
export interface ManagementItem {
  id: string; kind: string; title: string; year: number | null;
  catalogId: string | null; catalogName: string; versionCount: number; sizeBytes: number | null;
  availability: Availability; issueCount: number; affectedEpisodes: number; episodeCount: number;
  seasonCount: number; activeOperations: number; indexNumber: number | null;
  indexNumberEnd: number | null; parentIndexNumber: number | null;
}
export interface ManagementPage {
  items: ManagementItem[]; total: number; limit: number; offset: number;
  counts: Record<string, number>; reference: string;
}
export interface SourceDiagnostic {
  id: string; availability: Availability; reason: string | null;
  dolbyVisionFallback: boolean; dolbyVisionUnknown: boolean; matchesFilter: boolean;
}
export interface LibraryAvailability { id: string; availability: Availability; dolbyVisionFallback: boolean }
export const quickViews = [
  ["all", "All"], ["attention", "Needs attention"], ["unavailable", "No available version"],
  ["bluray", "Requires MKV"], ["dolby-vision", "Dolby Vision limitations"], ["active", "Active operations"],
] as const;
export const technicalFields = ["catalogId", "sourceKind", "resolution", "codec", "hdr", "dvProfile", "state", "minVersions", "maxVersions"] as const;
export const managementBase = "/api/proxy/api/management";
export const managementApi = {
  location: (id: string) => apiJson<{ id: string; kind: string; seriesId: string | null }>(`${managementBase}/items/${id}/location`),
  list: (query: string) => apiJson<ManagementPage>(`${managementBase}/items?${query}`),
  sources: (id: string, query: string) => apiJson<SourceDiagnostic[]>(`${managementBase}/items/${id}/sources?${query}`),
  availability: (ids: string[]) => apiJson<LibraryAvailability[]>(`/api/proxy/api/library/availability?${new URLSearchParams(ids.map(id => ["ids", id]))}`),
};

// UI state stays in the URL; only documented filter keys cross the BFF boundary.
export function managementQuery(search: URLSearchParams, kind: string, itemId?: string, seriesId?: string) {
  const query = new URLSearchParams({ kind, view: search.get("view") || "all", sort: search.get("sort") || (kind === "Episode" ? "episode" : "title"),
    descending: search.get("descending") === "true" ? "true" : "false", limit: search.get("limit") || "25", offset: search.get("offset") || "0" });
  for (const key of [...technicalFields, "search", "season"]) {
    const value = search.get(key); if (value) query.set(key, value);
  }
  if (itemId) query.set("itemId", itemId);
  if (seriesId) query.set("seriesId", seriesId);
  return query.toString();
}
export function managementHref(kind: string, id: string, seriesId?: string) {
  return kind === "Episode" && seriesId ? `/manage/series/${seriesId}?episode=${id}`
    : `/manage/${kind === "Series" ? "series" : "movies"}/${id}`;
}
export const availabilityLabels: Record<Availability, string> = {
  available: "Available", blocked: "Unavailable", pending: "Preparing", unknown: "Not assessed",
};
const reasons: Record<string, string> = {
  storage_unavailable: "Storage is unavailable", no_file: "No media file", requires_conversion: "Requires MKV",
  dv_profile_unknown: "Dolby Vision profile has not been assessed",
  media_unknown: "Media information is incomplete", preparation_unknown: "Preparation has not been assessed",
  unsupported_video_codec: "Video codec is unsupported", no_audio_track: "No audio track",
  unsupported_audio_codec: "Audio codec is unsupported", unsupported_dynamic_range: "Dynamic range is unsupported",
  packaging_pending: "Preparing for playback", packaging_unavailable: "Playback packaging is unavailable",
  packaging_unsupported_video: "Video cannot be packaged", packaging_unsupported_audio: "Audio cannot be packaged",
};
export function diagnosticReason(reason: string | null) { return reason ? (reasons[reason] ?? reason.replaceAll("_", " ")) : null; }
