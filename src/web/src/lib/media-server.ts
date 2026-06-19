// Typed client for the Media Server backend, reached through the same-origin BFF proxy
// (`/api/proxy/...` → internal `api` service). All calls carry the validated Host identity.

import { apiFetch, apiJson } from "@/lib/api";

const BASE = "/api/proxy/api";

export type CatalogType = "Movie" | "Series" | "Anime";

export interface Catalog {
  id: string;
  name: string;
  type: CatalogType;
  root: string;
  namingTemplate: string;
  defaultKeepSeeding: boolean;
  metadataLanguage: string | null;
  freeBytes: number;
  online: boolean;
  createdAt: string;
  updatedAt: string;
}

// A catalog-root mount the operator may place catalogs under. `path` is the absolute base the
// backend sees (a container path under docker, the host path under the dev runtime); `label` is a
// friendly name derived from it for the mount picker.
export interface CatalogMount {
  label: string;
  path: string;
}

// One catalog's footprint within a volume (`usedBytes` ≈ sum of tracked media sizes; approximate).
export interface CatalogUsageEntry {
  id: string;
  name: string;
  type: CatalogType;
  usedBytes: number;
}

// Per-volume storage usage: each catalog's footprint + free space. The bar scales to Σ(used) + free.
export interface CatalogVolumeUsage {
  label: string;
  freeBytes: number;
  catalogs: CatalogUsageEntry[];
}

export interface CreateCatalogInput {
  name: string;
  type: CatalogType;
  root: string;
  namingTemplate?: string;
  defaultKeepSeeding: boolean;
  metadataLanguage?: string | null;
}

export interface Download {
  id: string;
  infoHash: string;
  name: string | null;
  catalogId: string;
  state: string;
  keepSeeding: boolean;
  addedAt: string;
  completedAt: string | null;
  engineState: string | null;
  percentComplete: number | null;
  downloadRateBytesPerSecond: number | null;
  uploadRateBytesPerSecond: number | null;
  ratio: number | null;
  peers: number | null;
  sizeBytes: number | null;
}

export interface AddTorrentInput {
  catalogId: string;
  magnet?: string;
  torrentFileBase64?: string;
  keepSeeding?: boolean;
}

export interface IngestSourceFile {
  id: string;
  relativePath: string;
  sizeBytes: number;
  assignmentStatus: string;
  mediaItemId: string | null;
}

export interface MetadataCandidate {
  reference: { provider: string; id: string };
  title: string;
  year: number | null;
  score: number;
}

export interface IngestItem {
  id: string;
  catalogId: string;
  downloadId: string | null;
  downloadName: string | null;
  mediaItemId: string | null;
  stage: string;
  status: string;
  attemptCount: number;
  stagesCompleted: string[];
  lastError: string | null;
  nextAttemptAt: string | null;
  reviewCandidates: MetadataCandidate[];
  sourceFiles: IngestSourceFile[];
  createdAt: string;
  updatedAt: string;
}

export interface MatchInput {
  sourceFileId: string;
  kind: "Movie" | "Series" | "Season" | "Episode" | "Video";
  provider: string;
  providerId: string;
  title: string;
  year?: number | null;
  season?: number | null;
  episode?: number | null;
}

export interface MetadataSearchInput {
  title: string;
  year?: number | null;
  kind?: "Movie" | "Series" | "Season" | "Episode" | "Video" | null;
}

// Surface-neutral per-user playback state carried by every library DTO (mirrors the api UserItemDataDto).
export interface UserItemData {
  key: string;
  playbackPositionTicks: number;
  playCount: number;
  isFavorite: boolean;
  played: boolean;
  playedPercentage: number | null;
  lastPlayedDate: string | null;
  unplayedItemCount: number | null;
}

export interface LibraryItem {
  id: string;
  publicId: string | null;
  catalogId: string;
  kind: string;
  title: string;
  year: number | null;
  posterUrl: string | null;
  userData: UserItemData | null;
}

export interface MediaStream {
  type: string;
  index: number;
  codec: string | null;
  language: string | null;
  displayTitle: string | null;
  width: number | null;
  height: number | null;
  hdrFormat: string | null;
  channels: number | null;
  isDefault: boolean;
  isForced: boolean;
  isExternal: boolean;
}

export interface LibraryMediaSource {
  id: string;
  container: string;
  sizeBytes: number;
  bitrate: number | null;
  durationTicks: number;
  streams: MediaStream[];
}

export interface SeasonSummary {
  id: string;
  publicId: string | null;
  seasonNumber: number | null;
  title: string;
  episodeCount: number;
  userData: UserItemData | null;
}

export interface LibraryDetail {
  id: string;
  publicId: string | null;
  catalogId: string;
  kind: string;
  title: string;
  originalTitle: string | null;
  year: number | null;
  overview: string | null;
  tagline: string | null;
  genres: string[];
  officialRating: string | null;
  communityRating: number | null;
  runtimeTicks: number | null;
  indexNumber: number | null;
  parentIndexNumber: number | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  libraryPath: string | null;
  userData: UserItemData | null;
  mediaSources: LibraryMediaSource[];
  seasons: SeasonSummary[] | null;
}

export interface Episode {
  id: string;
  publicId: string | null;
  seasonNumber: number | null;
  episodeNumber: number | null;
  title: string;
  overview: string | null;
  runtimeTicks: number | null;
  posterUrl: string | null;
  userData: UserItemData | null;
}

// A Home-rail leaf (movie/episode) with its detail-page navigation target resolved.
export interface LibraryRailItem {
  id: string;
  kind: string;
  navId: string;
  navKind: string;
  title: string;
  subtitle: string | null;
  posterUrl: string | null;
  userData: UserItemData | null;
}

// Jellyfin/Infuse access credential (managed by the signed-in user).
export interface JellyfinCredentialStatus {
  hasCredential: boolean;
  username: string | null;
  createdAt: string | null;
  lastUsedAt: string | null;
  locked: boolean;
  permanentlyLocked: boolean;
  serverUrl: string | null;
}

export interface JellyfinCredentialSecret {
  username: string;
  pin: string | null;
  serverUrl: string | null;
}

export interface LibraryScanReport {
  catalogsScanned: number;
  sourcesChecked: number;
  missingFiles: number;
  missingPaths: string[];
}

async function send(path: string, method: string, body?: unknown): Promise<void> {
  await apiFetch(`${BASE}${path}`, {
    method,
    headers: body ? { "content-type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
}

export const mediaServer = {
  listCatalogs: () => apiJson<Catalog[]>(`${BASE}/catalogs`),
  listCatalogMounts: () => apiJson<CatalogMount[]>(`${BASE}/catalogs/mounts`),
  listCatalogUsage: () => apiJson<CatalogVolumeUsage[]>(`${BASE}/catalogs/usage`),
  createCatalog: (input: CreateCatalogInput) =>
    apiJson<Catalog>(`${BASE}/catalogs`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  deleteCatalog: (id: string) => send(`/catalogs/${id}`, "DELETE"),

  listDownloads: () => apiJson<Download[]>(`${BASE}/torrents`),
  addTorrent: (input: AddTorrentInput) =>
    apiJson<Download>(`${BASE}/torrents/add`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  pauseDownload: (id: string) => send(`/torrents/${id}/pause`, "POST"),
  resumeDownload: (id: string) => send(`/torrents/${id}/resume`, "POST"),
  stopSeeding: (id: string) => send(`/torrents/${id}/stop-seeding`, "POST"),
  removeDownload: (id: string, deleteFiles: boolean) =>
    send(`/torrents/${id}?deleteFiles=${deleteFiles}`, "DELETE"),

  listIngest: () => apiJson<IngestItem[]>(`${BASE}/ingest`),
  retryIngest: (id: string) => send(`/ingest/${id}/retry`, "POST"),
  matchIngest: (id: string, input: MatchInput) => send(`/ingest/${id}/match`, "POST", input),
  searchIngest: (id: string, input: MetadataSearchInput) =>
    apiJson<MetadataCandidate[]>(`${BASE}/ingest/${id}/search`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  deleteIngest: (id: string) => send(`/ingest/${id}`, "DELETE"),

  listLibrary: () => apiJson<LibraryItem[]>(`${BASE}/library`),
  getLibraryDetail: (id: string) => apiJson<LibraryDetail>(`${BASE}/library/${id}`),
  listEpisodes: (seriesId: string, seasonId?: string) =>
    apiJson<Episode[]>(`${BASE}/library/${seriesId}/episodes${seasonId ? `?seasonId=${seasonId}` : ""}`),
  listRecent: () => apiJson<LibraryItem[]>(`${BASE}/library/recent`),
  listResume: () => apiJson<LibraryRailItem[]>(`${BASE}/library/resume`),
  listNextUp: () => apiJson<LibraryRailItem[]>(`${BASE}/library/nextup`),
  setPlayed: (id: string, played: boolean) =>
    apiJson<UserItemData>(`${BASE}/library/${id}/played`, { method: played ? "POST" : "DELETE" }),
  setFavorite: (id: string, favorite: boolean) =>
    apiJson<UserItemData>(`${BASE}/library/${id}/favorite`, { method: favorite ? "POST" : "DELETE" }),
  deleteLibraryItem: (id: string, deleteFiles: boolean) =>
    send(`/library/${id}?deleteFiles=${deleteFiles}`, "DELETE"),
  refreshMetadata: (id: string) => send(`/library/${id}/refresh`, "POST"),
  scanLibrary: () => apiJson<LibraryScanReport>(`${BASE}/library/scan`, { method: "POST" }),

  getJellyfinCredential: () => apiJson<JellyfinCredentialStatus>(`${BASE}/jellyfin/credential`),
  createJellyfinCredential: (pin?: string) =>
    apiJson<JellyfinCredentialSecret>(`${BASE}/jellyfin/credential`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ pin: pin && pin.length > 0 ? pin : null }),
    }),
  revokeJellyfinCredential: () => send(`/jellyfin/credential`, "DELETE"),
};
