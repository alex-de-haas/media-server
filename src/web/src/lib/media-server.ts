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

// Engine-wide VPN tunnel status. `connected` is the primary signal; `exitIp`/`exitCountry` are a
// best-effort proof that traffic egresses through the tunnel and may be null. The whole object is null
// when downloading runs in-process (no VPN to report) — the UI hides the indicator in that case.
export interface VpnStatus {
  connected: boolean;
  tunnelInterface: string | null;
  tunnelAddress: string | null;
  exitIp: string | null;
  exitCountry: string | null;
  checkedAt: string;
}

export interface IngestSourceFile {
  id: string;
  relativePath: string;
  sizeBytes: number;
  assignmentStatus: string;
  mediaItemId: string | null;
  // Name-parsed hints from the backend (computed from relativePath) used to pre-fill the review dialog:
  // the corrected title to search, and per-file season/episode. season/episode are null for movies or
  // when the filename has no SxxEyy pattern.
  parsedTitle: string;
  parsedYear: number | null;
  parsedSeason: number | null;
  parsedEpisode: number | null;
}

export interface MetadataCandidate {
  reference: { provider: string; id: string };
  title: string;
  year: number | null;
  score: number;
  // Ready-to-render poster thumbnail URL, or null when the provider returned no poster.
  posterUrl: string | null;
}

export interface IngestItem {
  id: string;
  catalogId: string;
  downloadId: string | null;
  downloadName: string | null;
  mediaTitle: string | null;
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

// Operator-editable application settings (persisted server-side, distinct from manifest config).
export interface AppSettings {
  // Release-group / tag tokens stripped from a file name before metadata identification.
  customReleaseGroups: string[];
}

// Reassigns an already-published leaf (movie/episode) to a corrected identity; the backend rebuilds the
// library hardlink and prunes the orphaned old item. For episodes, the identity is the owning *series*
// plus season/episode numbers (same shape the ingest match uses).
export interface RemapInput {
  kind: "Movie" | "Episode";
  provider: string;
  providerId: string;
  title: string;
  year?: number | null;
  season?: number | null;
  episode?: number | null;
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

// A movie franchise/collection tile for the Collections grid (TMDb belongs_to_collection).
export interface CollectionSummary {
  id: string;
  name: string;
  posterUrl: string | null;
  itemCount: number;
}

// A collection detail: the franchise artwork plus its in-library movies as library cards.
export interface CollectionDetail {
  id: string;
  name: string;
  posterUrl: string | null;
  backdropUrl: string | null;
  items: LibraryItem[];
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
  versionName: string | null;
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
  tmdbId: string | null;
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
  // TMDb title logo (styled title as a transparent PNG), language-matched when available.
  logoUrl: string | null;
  libraryPath: string | null;
  userData: UserItemData | null;
  mediaSources: LibraryMediaSource[];
  seasons: SeasonSummary[] | null;
  // Distributor/network logos (Netflix, Apple TV+, …) for series; null for movies.
  networks: Network[] | null;
  // Production status (Released, Ended, Returning Series, …).
  status: string | null;
  // Number of community ratings backing `communityRating`.
  voteCount: number | null;
  // Total seasons/episodes per TMDb (series only).
  seasonCount: number | null;
  episodeCount: number | null;
  // Franchise/collection a movie belongs to (e.g. "The Lord of the Rings Collection").
  collectionName: string | null;
  homepage: string | null;
  // IMDb id (tt…) for cross-linking.
  imdbId: string | null;
  // Best YouTube trailer URL, or null when none.
  trailerUrl: string | null;
  cast: CastMember[];
  // Director(s) for movies.
  directors: string[];
  // Creator(s) for series.
  creators: string[];
  // Production companies / studios.
  studios: Studio[];
  // TMDb keyword tags.
  keywords: string[];
}

// A TV network/distributor surfaced on series detail.
export interface Network {
  name: string;
  logoUrl: string | null;
}

// A production company / studio (same shape as Network).
export interface Studio {
  name: string;
  logoUrl: string | null;
}

// A cast member: actor, the character they play (when known), and a profile photo. `provider`/
// `providerId` are the stable person identity used to link to the person page; cast is read from the
// Person join server-side, so the identity is always present.
export interface CastMember {
  provider: string;
  providerId: string;
  name: string;
  character: string | null;
  profileUrl: string | null;
}

// One entry in a person's filmography: a movie or series in the library they're credited on. `id` is the
// media item id and doubles as the detail-page navigation target.
export interface PersonFilmographyEntry {
  id: string;
  kind: string;
  title: string;
  year: number | null;
  posterUrl: string | null;
  // The portrayed character for a cast credit; null for crew.
  character: string | null;
  // The crew job (e.g. "Director") for a crew credit; null for cast.
  job: string | null;
}

// A person's crew filmography for one department (e.g. "Directing"), entries newest first.
export interface PersonCrewGroup {
  department: string;
  credits: PersonFilmographyEntry[];
}

// A person page: provider-identified details plus their filmography within the library, split into cast
// (acting) credits and crew credits grouped by department.
export interface Person {
  provider: string;
  providerId: string;
  name: string;
  profileUrl: string | null;
  biography: string | null;
  knownForDepartment: string | null;
  // Birth/death dates as the provider returns them (e.g. "1974-11-11"); null when unknown.
  birthday: string | null;
  deathday: string | null;
  placeOfBirth: string | null;
  cast: PersonFilmographyEntry[];
  crew: PersonCrewGroup[];
}

export interface Episode {
  id: string;
  publicId: string | null;
  seriesTmdbId: string | null;
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

// Result of a per-catalog import scan: orphan media files under the root are ingested from identify.
export interface LibraryImportReport {
  filesScanned: number;
  imported: number;
  skipped: number;
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
  scanCatalog: (id: string) => apiJson<LibraryImportReport>(`${BASE}/catalogs/${id}/scan`, { method: "POST" }),

  listDownloads: () => apiJson<Download[]>(`${BASE}/torrents`),
  getVpnStatus: () => apiJson<VpnStatus | null>(`${BASE}/vpn`),
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
  getSettings: () => apiJson<AppSettings>(`${BASE}/settings`),
  updateSettings: (input: AppSettings) =>
    apiJson<AppSettings>(`${BASE}/settings`, {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),

  deleteIngest: (id: string) => send(`/ingest/${id}`, "DELETE"),
  deleteDoneIngest: async () =>
    (await apiJson<{ removed: number }>(`${BASE}/ingest/done`, { method: "DELETE" })).removed,

  listLibrary: () => apiJson<LibraryItem[]>(`${BASE}/library`),
  getLibraryDetail: (id: string) => apiJson<LibraryDetail>(`${BASE}/library/${id}`),
  listEpisodes: (seriesId: string, seasonId?: string) =>
    apiJson<Episode[]>(`${BASE}/library/${seriesId}/episodes${seasonId ? `?seasonId=${seasonId}` : ""}`),
  listCollections: () => apiJson<CollectionSummary[]>(`${BASE}/library/collections`),
  getCollectionDetail: (id: string) => apiJson<CollectionDetail>(`${BASE}/library/collections/${id}`),
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
  remapLibraryItem: (id: string, input: RemapInput) =>
    apiJson<{ id: string }>(`${BASE}/library/${id}/remap`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  scanLibrary: () => apiJson<LibraryScanReport>(`${BASE}/library/scan`, { method: "POST" }),

  // Person page, keyed by the provider identity its cast members carry (CastMember.provider/providerId).
  getPerson: (provider: string, providerId: string) =>
    apiJson<Person>(`${BASE}/persons/${provider}/${providerId}`),

  searchMetadata: (input: MetadataSearchInput) =>
    apiJson<MetadataCandidate[]>(`${BASE}/metadata/search`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),

  getJellyfinCredential: () => apiJson<JellyfinCredentialStatus>(`${BASE}/jellyfin/credential`),
  createJellyfinCredential: (pin?: string) =>
    apiJson<JellyfinCredentialSecret>(`${BASE}/jellyfin/credential`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ pin: pin && pin.length > 0 ? pin : null }),
    }),
  revokeJellyfinCredential: () => send(`/jellyfin/credential`, "DELETE"),
};
