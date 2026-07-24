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
  // Extended live stats (null when the engine has no active manager for this download).
  seeds: number | null;
  leeches: number | null;
  availablePeers: number | null;
  downloadedBytes: number | null;
  uploadedBytes: number | null;
  remainingBytes: number | null;
  totalPieces: number | null;
  completePieces: number | null;
  etaSeconds: number | null;
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

// Where an already-mapped source file points. provider/providerId carry the identity used for the
// mapping (an episode's identity is its series' provider reference) so the review dialog can pre-select
// the same series; null for extras, which have no provider identity of their own.
export interface IngestAssignedMedia {
  kind: string;
  title: string;
  season: number | null;
  episode: number | null;
  seriesTitle: string | null;
  provider: string | null;
  providerId: string | null;
}

export interface IngestSourceFile {
  id: string;
  relativePath: string;
  sizeBytes: number;
  assignmentStatus: string;
  mediaItemId: string | null;
  // External audio track (an .mka/.ac3 dub riding alongside the videos): matching it to an episode/movie
  // means "merge into that item's video file" rather than importing it as content of its own.
  isAudio: boolean;
  // The current mapping for a Confirmed file (shown and re-decidable while the batch is in review).
  assigned: IngestAssignedMedia | null;
  // Name-parsed hints from the backend (computed from relativePath) used to pre-fill the review dialog:
  // the corrected title to search, and per-file season/episode. season/episode are null for movies or
  // when the filename has no SxxEyy pattern.
  parsedTitle: string;
  parsedYear: number | null;
  parsedSeason: number | null;
  parsedEpisode: number | null;
  // Extras classification (creditless OP/EDs, PVs, menus, …), null for regular content. When set, the
  // review dialog pre-suggests attaching the file to its series as an extra titled extraTitle — or
  // skipping it when extraSuggestSkip is true (disc menus, commercials).
  extraKind: string | null;
  extraTitle: string | null;
  extraSuggestSkip: boolean;
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
  // Operator-pinned target identity (null when nothing is pinned): Identify resolves straight to it instead
  // of parsing + searching. targetKind is "Movie" or "Series".
  targetProvider: string | null;
  targetProviderId: string | null;
  targetKind: string | null;
  targetTitle: string | null;
  targetYear: number | null;
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

// One confirmed identity (the series for episodes, the movie itself otherwise) applied to every file in
// the batch — only the per-file season/episode vary. Files already auto-matched may be re-matched this
// way while the item is still in review.
export interface MatchInput {
  kind: "Movie" | "Series" | "Season" | "Episode" | "Video";
  provider: string;
  providerId: string;
  title: string;
  year?: number | null;
  files: { sourceFileId: string; season?: number | null; episode?: number | null }[];
}

export interface MetadataSearchInput {
  title: string;
  year?: number | null;
  kind?: "Movie" | "Series" | "Season" | "Episode" | "Video" | null;
}

// Attaches NeedsReview files to a series (by provider identity) as playable extras. season optionally
// parents them under that season; each file's own title derives server-side from its classification.
export interface AssignExtrasInput {
  sourceFileIds: string[];
  provider: string;
  providerId: string;
  title: string;
  year?: number | null;
  season?: number | null;
}

// Pins a target identity on an ingest item before/while it downloads so Identify resolves straight to it.
// kind is the movie's own kind, or the owning series (per-file season/episode still come from the file name).
export interface PinInput {
  provider: string;
  providerId: string;
  kind: "Movie" | "Series";
  title: string;
  year?: number | null;
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

export interface ListLibraryOptions {
  kind?: "Movie" | "Series";
  catalogId?: string;
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
  title: string | null;
  width: number | null;
  height: number | null;
  hdrFormat: string | null;
  channels: number | null;
  // Secondary specs shown under each track: codec profile, video frame rate, bit depth, audio sample rate (Hz).
  profile: string | null;
  frameRate: number | null;
  bitDepth: number | null;
  sampleRate: number | null;
  isDefault: boolean;
  isForced: boolean;
  isExternal: boolean;
}

export interface LibraryMediaSource {
  id: string;
  versionName: string | null;
  // On-disk file name (with extension); read-only, shown to tell sources apart.
  fileName: string;
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
  // Name and root host path of the catalog this item lives in.
  catalogName: string;
  catalogRoot: string;
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
  // Last episode covered when one file holds a consecutive range (S01E01-E02); null for a single episode.
  indexNumberEnd: number | null;
  parentIndexNumber: number | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  // TMDb title logo (styled title as a transparent PNG), language-matched when available.
  logoUrl: string | null;
  libraryPath: string | null;
  // Catalog-root-relative folder that holds this title's files; null when nothing is on disk yet.
  contentPath: string | null;
  userData: UserItemData | null;
  // The source pinned to play by default (first in `mediaSources`); null when no preference is set.
  defaultSourceId: string | null;
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
  // Last episode covered when this one file holds a consecutive range (a "double episode"): the item is
  // numbered 1 with `episodeNumberEnd` 2 and there is no separate item for episode 2. Null otherwise.
  // `title` stays the first episode's — the provider has no combined title for the range.
  episodeNumberEnd: number | null;
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

// Watch-history provider connections (Trakt today), managed by the signed-in user. No response on
// any of these ever carries a token, refresh token, or device code — only display material.
export type WatchHistoryConnectionStatus = "Connected" | "RequiresReconnect";

export interface WatchHistoryConnection {
  providerKey: string;
  status: WatchHistoryConnectionStatus;
  accountName: string | null;
  connectedAt: string;
  lastDeliveryAt: string | null;
  lastSyncAt: string | null;
  lastError: string | null;
}

export interface WatchHistoryProvider {
  key: string;
  displayName: string;
  // The operator has supplied this provider's application settings; without it, Connect is unavailable.
  isConfigured: boolean;
  supportsExactTimestamps: boolean;
  connection: WatchHistoryConnection | null;
}

export type WatchHistoryAuthorizationState =
  | "Pending"
  | "Approved"
  | "Denied"
  | "Expired"
  | "SlowDown";

export interface WatchHistoryAuthorization {
  state: WatchHistoryAuthorizationState;
  // Safe to display by design: the activation code the user types on the provider's site, and where.
  userCode: string | null;
  verificationUrl: string | null;
  expiresAt: string | null;
  pollIntervalSeconds: number | null;
  connection: WatchHistoryConnection | null;
}

// How one local item compares with the provider. Mirrors the API's classification enum names.
export type WatchHistorySyncClassification =
  | "InSync"
  | "RemoteOnly"
  | "LocalOnly"
  | "LocalUnwatchedWithHistory"
  | "UnidentifiedLocally"
  | "AmbiguousLocalIdentity";

export interface WatchHistorySyncEntry {
  mediaItemId: string;
  title: string;
  classification: WatchHistorySyncClassification;
  localPlayCount: number;
  remotePlayCount: number;
}

export interface WatchHistorySyncPreview {
  runId: string;
  // Keyed by classification name; a key is absent when its tally is zero.
  counts: Partial<Record<WatchHistorySyncClassification, number>>;
  sample: WatchHistorySyncEntry[];
  hasPendingOutboundWork: boolean;
  hasTerminalOutboundWork: boolean;
  aggregateCountsMayCollapse: boolean;
}

// Why an item was left untouched during apply. Mirrors the API's skip-reason enum names.
export type WatchHistorySyncSkip =
  | "LocalStateChangedDuringSync"
  | "AmbiguousLocalIdentity"
  | "UnidentifiedLocally"
  | "ExportFailed";

export interface WatchHistorySyncResult {
  imported: number;
  exported: number;
  unchanged: number;
  skipped: Partial<Record<WatchHistorySyncSkip, number>>;
}

export interface WatchHistorySyncScope {
  catalogIds?: string[];
  kinds?: Array<"Movie" | "Episode">;
}

/** One completed play on the Watched calendar. Episodes carry their series' title and poster. */
export interface WatchHistoryCalendarEvent {
  entryId: string;
  watchedAt: string;
  mediaItemId: string;
  publicId: string | null;
  kind: "Movie" | "Episode";
  title: string;
  posterUrl: string | null;
  seriesId: string | null;
  seriesTitle: string | null;
  seasonNumber: number | null;
  episodeNumber: number | null;
  origin: "LocalPlayback" | "Manual" | "ProviderSync" | "Legacy";
}

/** A watched mark with no date: shown in a list, never placed on a guessed day. */
export interface WatchHistoryUndatedEntry {
  entryId: string;
  mediaItemId: string;
  publicId: string | null;
  kind: "Movie" | "Episode";
  title: string;
  posterUrl: string | null;
  seriesTitle: string | null;
  seasonNumber: number | null;
  episodeNumber: number | null;
  origin: "LocalPlayback" | "Manual" | "ProviderSync" | "Legacy";
}

export interface WatchHistoryCalendarResponse {
  events: WatchHistoryCalendarEvent[];
  /** Timeless marks get no date, so they are counted per kind instead of placed on the grid. */
  undated: { movies: number; episodes: number };
  /** The most recent dated play overall — lets an empty month offer a jump. */
  latestWatchedAt: string | null;
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

// A catalog whose metadata refresh is currently in flight, with its job id and 0–100 progress.
export interface CatalogRefreshJob {
  catalogId: string;
  jobId: string;
  progress: number;
}

// A move of a top-level item into another catalog that's currently in flight, with its 0–100 progress and
// the labels the Activity view shows. title/targetCatalogName are null for a move stranded by a restart.
// bytesPerSecond/etaSeconds are the live copy throughput, pushed on each progress tick over SSE; they're
// absent until the first tick (and always for an instant same-volume rename).
export interface LibraryMoveJob {
  itemId: string;
  jobId: string;
  progress: number;
  title: string | null;
  targetCatalogName: string | null;
  bytesPerSecond?: number | null;
  etaSeconds?: number | null;
  // True while the move is admitted but waiting behind the one that's actively copying (moves run one at a
  // time). Comes from the seeded active list; a progress tick proves it's running and clears it.
  queued?: boolean;
}

async function send(path: string, method: string, body?: unknown): Promise<void> {
  await apiFetch(`${BASE}${path}`, {
    method,
    headers: body ? { "content-type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
}

// A transcode job: re-encode one movie source into a smaller sibling version, run by the external
// transcode-engine. Persisted facts plus the live engine snapshot (fps/speed/eta/outputSize are null
// unless the job is running).
export interface TranscodeJob {
  id: string;
  engineJobId: string;
  mediaSourceId: string;
  mediaItemId: string;
  name: string | null;
  inputPath: string;
  outputPath: string;
  videoCodec: string;
  hardwareAcceleration: string;
  crf: number | null;
  state: string;
  percentComplete: number;
  error: string | null;
  createdAt: string;
  completedAt: string | null;
  fps: number | null;
  speed: number | null;
  etaSeconds: number | null;
  outputSizeBytes: number | null;
}

export interface CreateTranscodeInput {
  sourceId: string;
  videoCodec?: string;
  hardwareAcceleration?: string;
  crf?: number | null;
  /** Downscale target height; omit to keep the source resolution. Ignored when videoCodec is "copy". */
  maxHeight?: number | null;
  /** Source stream indexes to copy; omit to copy all of that type. */
  audioStreamIndexes?: number[];
  subtitleStreamIndexes?: number[];
  /** Mark one copied track as the container default. */
  defaultAudioStreamIndex?: number | null;
  defaultSubtitleStreamIndex?: number | null;
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
  // Kick off a background refresh of every identified item's metadata in the catalog; progress streams over SSE.
  refreshCatalogMetadata: (id: string) =>
    apiJson<{ jobId: string }>(`${BASE}/catalogs/${id}/refresh-metadata`, { method: "POST" }),
  listActiveCatalogRefreshes: () => apiJson<CatalogRefreshJob[]>(`${BASE}/catalogs/refresh-metadata/active`),

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

  listTranscodeJobs: () => apiJson<TranscodeJob[]>(`${BASE}/transcode`),
  createTranscodeJob: (input: CreateTranscodeInput) =>
    apiJson<TranscodeJob>(`${BASE}/transcode`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  cancelTranscodeJob: (id: string) => send(`/transcode/${id}/cancel`, "POST"),
  removeTranscodeJob: (id: string) => send(`/transcode/${id}`, "DELETE"),

  listIngest: () => apiJson<IngestItem[]>(`${BASE}/ingest`),
  retryIngest: (id: string) => send(`/ingest/${id}/retry`, "POST"),
  matchIngest: (id: string, input: MatchInput) => send(`/ingest/${id}/match`, "POST", input),
  // Skip files with no matchable identity (creditless OP/EDs and other extras) so the batch proceeds
  // without them; skipped files are never imported.
  skipIngestFiles: (id: string, sourceFileIds: string[]) => send(`/ingest/${id}/skip`, "POST", { sourceFileIds }),
  // Attach files to a series as playable extras (the keep-it alternative to skipping): each file becomes
  // a non-episode video under the series' extras/ folder, titled from its classification.
  assignIngestExtras: (id: string, input: AssignExtrasInput) => send(`/ingest/${id}/extras`, "POST", input),
  // Pin (or re-pin) the target identity; clear it back to the auto-identify path with unpinIngest.
  pinIngest: (id: string, input: PinInput) => send(`/ingest/${id}/pin`, "POST", input),
  unpinIngest: (id: string) => send(`/ingest/${id}/pin`, "DELETE"),
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

  listLibrary: ({ kind, catalogId }: ListLibraryOptions = {}) => {
    const query = new URLSearchParams();
    if (kind) query.set("kind", kind);
    if (catalogId) query.set("catalogId", catalogId);
    const queryString = query.toString();
    const suffix = queryString ? `?${queryString}` : "";
    return apiJson<LibraryItem[]>(`${BASE}/library${suffix}`);
  },
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
  // Delete one media source / version (admin); deleteFile also erases the file (used for transcode "replace").
  deleteMediaSource: (sourceId: string, deleteFile: boolean) =>
    send(`/library/sources/${sourceId}?deleteFile=${deleteFile}`, "DELETE"),
  // Pin the version that plays by default (pass null to clear); admin only.
  setDefaultSource: (itemId: string, sourceId: string | null) =>
    send(`/library/${itemId}/default-source`, "PUT", { sourceId }),
  // Rename a movie source's version (pass null/empty to clear); admin only. Renames the file on disk to
  // "Title (Year) - {version}.ext" and syncs the stored label.
  setSourceVersion: (sourceId: string, versionName: string | null) =>
    send(`/library/sources/${sourceId}/version`, "PUT", { versionName }),
  refreshMetadata: (id: string) => send(`/library/${id}/refresh`, "POST"),
  refreshMedia: (id: string) => send(`/library/${id}/refresh-media`, "POST"),
  remapLibraryItem: (id: string, input: RemapInput) =>
    apiJson<{ id: string }>(`${BASE}/library/${id}/remap`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  // Move a published movie/series into another type-compatible catalog; runs as a background job (progress
  // over SSE) and returns its job id.
  moveLibraryItem: (id: string, targetCatalogId: string) =>
    apiJson<{ jobId: string }>(`${BASE}/library/${id}/move`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ targetCatalogId }),
    }),
  // In-flight cross-catalog moves; seeds the Activity progress list, then kept live by RealtimeBridge over SSE.
  listActiveMoves: () => apiJson<LibraryMoveJob[]>(`${BASE}/library/move/active`),
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

  // Watch-history providers. Every route acts on the caller; none takes an app-user id.
  listWatchHistoryProviders: () =>
    apiJson<WatchHistoryProvider[]>(`${BASE}/watch-history/providers`),
  // The range is the visible grid as UTC instants; the server returns raw plays and the browser
  // groups them by its own local days.
  watchHistoryUndated: () =>
    apiJson<WatchHistoryUndatedEntry[]>(`${BASE}/watch-history/calendar/undated`),
  watchHistoryCalendar: (from: string, toExclusive: string) =>
    apiJson<WatchHistoryCalendarResponse>(
      `${BASE}/watch-history/calendar?from=${encodeURIComponent(from)}&toExclusive=${encodeURIComponent(toExclusive)}`,
    ),
  startWatchHistoryAuthorization: (providerKey: string) =>
    apiJson<WatchHistoryAuthorization>(
      `${BASE}/watch-history/connections/${providerKey}/authorization/start`,
      { method: "POST" },
    ),
  pollWatchHistoryAuthorization: (providerKey: string) =>
    apiJson<WatchHistoryAuthorization>(
      `${BASE}/watch-history/connections/${providerKey}/authorization/poll`,
      { method: "POST" },
    ),
  previewWatchHistorySync: (providerKey: string, scope?: WatchHistorySyncScope) =>
    apiJson<WatchHistorySyncPreview>(
      `${BASE}/watch-history/connections/${providerKey}/sync/preview`,
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(scope ?? {}),
      },
    ),
  applyWatchHistorySync: (providerKey: string, runId: string) =>
    apiJson<WatchHistorySyncResult>(
      `${BASE}/watch-history/connections/${providerKey}/sync/apply`,
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ runId }),
      },
    ),
  disconnectWatchHistoryProvider: (providerKey: string) =>
    send(`/watch-history/connections/${providerKey}`, "DELETE"),
};
