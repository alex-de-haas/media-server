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
  mediaItemId: string | null;
  stage: string;
  status: string;
  attemptCount: number;
  stagesCompleted: string[];
  lastError: string | null;
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

export interface LibraryItem {
  id: string;
  publicId: string | null;
  catalogId: string;
  kind: string;
  title: string;
  year: number | null;
  libraryPath: string | null;
  posterUrl: string | null;
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

async function send(path: string, method: string, body?: unknown): Promise<void> {
  await apiFetch(`${BASE}${path}`, {
    method,
    headers: body ? { "content-type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
}

export const mediaServer = {
  listCatalogs: () => apiJson<Catalog[]>(`${BASE}/catalogs`),
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

  listLibrary: () => apiJson<LibraryItem[]>(`${BASE}/library`),

  getJellyfinCredential: () => apiJson<JellyfinCredentialStatus>(`${BASE}/jellyfin/credential`),
  createJellyfinCredential: (pin?: string) =>
    apiJson<JellyfinCredentialSecret>(`${BASE}/jellyfin/credential`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ pin: pin && pin.length > 0 ? pin : null }),
    }),
  revokeJellyfinCredential: () => send(`/jellyfin/credential`, "DELETE"),
};
