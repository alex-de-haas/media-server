import type { Page } from "@playwright/test";

// Mocks the same-origin BFF (`/api/auth/session`, `/api/proxy/api/**`) so the UI can be driven without
// Hosty Core. Each test passes only the surfaces it cares about; everything else returns an empty list.

const session = (role: "admin" | "user") => ({
  userId: "u1",
  email: "tester@example.com",
  displayName: "Tester",
  role,
});

/** Per-user state for one item, as the API projects it. Exported so a spec can seed a detail with it. */
export function aUserData(overrides: Record<string, unknown> = {}) {
  return {
    key: "k",
    playbackPositionTicks: 0,
    playCount: 0,
    isFavorite: false,
    played: false,
    playedPercentage: null,
    lastPlayedDate: null,
    unplayedItemCount: null,
    userRating: null,
    ...overrides,
  };
}

/**
 * A payload that may change between requests. Pass a function when a test mutates state and then
 * relies on a refetch showing it — a fixed object replays the old answer and hides the bug.
 */
type Served<T> = T | (() => T);

function serve<T>(value: Served<T> | undefined, fallback: T): T {
  if (typeof value === "function") return (value as () => T)();
  return value ?? fallback;
}

export interface AppMock {
  role?: "admin" | "user" | null; // null → unauthenticated (401, or sessionStatus)
  sessionStatus?: 401 | 403; // failure status when role is null (default 401)
  recoveryOrigin?: string; // browser-reachable Core origin in the failure body (default null)
  library?: unknown[] | { status: number };
  recent?: unknown[];
  resume?: unknown[];
  nextup?: unknown[];
  detail?: Record<string, unknown>;
  episodes?: Record<string, unknown[]>;
  childDelete?: { seasonRemoved: boolean; seriesRemoved: boolean }; // DELETE /library/{episodes,seasons}/{id}
  catalogs?: unknown[];
  catalogMounts?: unknown[]; // GET /catalogs/mounts — the mount picker in the add/re-anchor dialogs
  downloads?: unknown[];
  ingest?: unknown[];
  vpn?: unknown;
  metadataSearch?: unknown[];
  remapTargetId?: string; // id returned by POST /library/{id}/remap
  releaseCalendar?: unknown[]; // GET /watchlist/calendar
  watchlist?: unknown[]; // GET /watchlist
  titlePreview?: Record<string, unknown>; // GET /metadata/{provider}/{id}, keyed by provider id
  watchHistoryCalendar?: Served<Record<string, unknown>>; // GET /watch-history/calendar (an envelope, not a list)
  watchHistoryUndated?: Served<Record<string, unknown>>; // GET /watch-history/calendar/undated ({ entries, total })
  deleteWatchHistoryEntry?: (entryId: string) => void; // DELETE /watch-history/entries/{id} — runs before the 204
  setWatchHistoryEntryTime?: (entryId: string, watchedAt: string) => void; // PATCH /watch-history/entries/{id}
  logWatch?: (itemId: string, watchedAt: string) => void; // POST /library/{id}/watches — runs before the 200
  // GET /recommendations. Merged over the envelope's defaults, so a test names only what it cares
  // about and still gets a well-formed feed.
  recommendations?: Record<string, unknown>;
  transcodeAvailable?: boolean; // GET /transcode/availability — gates the Convert, Merge and backfill controls
  transcodeLanguages?: string[]; // GET /transcode/languages — what the language field validates against
  mediaBackfill?: { itemsRefreshed: number; remaining: number; sidecarsFilled: number }; // POST /library/backfill-media
}

export async function setupApp(page: Page, mock: AppMock = {}): Promise<void> {
  const role = mock.role === undefined ? "admin" : mock.role;

  await page.route("**/api/auth/session", (route) =>
    role
      ? route.fulfill({ json: session(role) })
      : route.fulfill({
          status: mock.sessionStatus ?? 401,
          json: {
            error: mock.sessionStatus === 403 ? "forbidden" : "unauthenticated",
            recovery: {
              appId: "com.haas.media-server",
              corePublicOrigin: mock.recoveryOrigin ?? null,
            },
          },
        }),
  );

  await page.route("**/api/proxy/api/**", async (route) => {
    const requestUrl = new URL(route.request().url());
    const path = requestUrl.pathname.replace("/api/proxy/api", "");
    const method = route.request().method();

    if (path === "/library") {
      if (mock.library && !Array.isArray(mock.library)) {
        return route.fulfill({ status: mock.library.status, json: { error: "boom" } });
      }
      const kind = requestUrl.searchParams.get("kind");
      const catalogId = requestUrl.searchParams.get("catalogId");
      const items = (mock.library ?? []).filter((item) => {
        const record = item as Record<string, unknown>;
        return (!kind || record.kind === kind) && (!catalogId || record.catalogId === catalogId);
      });
      return route.fulfill({ json: items });
    }
    if (path === "/library/recent") return route.fulfill({ json: mock.recent ?? [] });
    if (path === "/library/resume") return route.fulfill({ json: mock.resume ?? [] });
    if (path === "/library/nextup") return route.fulfill({ json: mock.nextup ?? [] });
    if (path === "/catalogs") return route.fulfill({ json: mock.catalogs ?? [] });
    if (path === "/catalogs/mounts") return route.fulfill({ json: mock.catalogMounts ?? [] });
    // Re-anchor answers with the catalog it rewrote; the UI only refetches, so echoing the first is enough.
    const anchoredId = path.match(/^\/catalogs\/([^/]+)\/anchor$/)?.[1];
    if (anchoredId && method === "POST") {
      return route.fulfill({ json: (mock.catalogs ?? [])[0] ?? {} });
    }
    if (path === "/torrents") return route.fulfill({ json: mock.downloads ?? [] });
    if (path === "/ingest") return route.fulfill({ json: mock.ingest ?? [] });
    if (path === "/vpn") return route.fulfill({ json: mock.vpn ?? null });
    if (path.endsWith("/played")) return route.fulfill({ json: aUserData({ played: method === "POST" }) });
    // A logged watch carries a body, unlike the played toggle: the instant is the whole point of it.
    const loggedWatchItemId =
      method === "POST" ? path.match(/^\/library\/([^/]+)\/watches$/)?.[1] : undefined;
    if (loggedWatchItemId) {
      mock.logWatch?.(loggedWatchItemId, (route.request().postDataJSON() as { watchedAt: string }).watchedAt);
      return route.fulfill({ json: aUserData({ played: true, playCount: 1 }) });
    }
    if (path.endsWith("/favorite")) return route.fulfill({ json: aUserData({ isFavorite: method === "POST" }) });
    if (path.endsWith("/rating")) {
      // PUT carries the stars; DELETE clears back to unrated, which is not the same as one star.
      const rating = method === "PUT" ? (route.request().postDataJSON() as { rating: number }).rating : null;
      return route.fulfill({ json: aUserData({ userRating: rating }) });
    }

    if (path === "/watchlist/calendar") return route.fulfill({ json: mock.releaseCalendar ?? [] });
    if (path === "/watchlist") {
      // POST is a track; the dialogs only need it to succeed.
      return method === "POST"
        ? route.fulfill({ json: { ...(mock.watchlist?.[0] ?? {}), id: "w-new", title: "Tracked" } })
        : route.fulfill({ json: mock.watchlist ?? [] });
    }
    // An envelope rather than a list, so it cannot fall through to the empty-array catch-all.
    if (path === "/recommendations") {
      return route.fulfill({
        json: {
          items: [],
          sources: [],
          selectedSources: [],
          popularityBias: 0,
          maxPopularityBias: 2,
          ...(mock.recommendations ?? {}),
        },
      });
    }
    if (
      path === "/recommendations/hide" ||
      path === "/recommendations/sources" ||
      path === "/recommendations/popularity-bias"
    ) {
      return route.fulfill({ status: 204, body: "" });
    }

    if (path === "/watch-history/calendar/undated") {
      return route.fulfill({ json: serve(mock.watchHistoryUndated, { entries: [], total: 0 }) });
    }
    if (path === "/watch-history/calendar") {
      return route.fulfill({
        json: serve(mock.watchHistoryCalendar, {
          events: [],
          undated: { movies: 0, episodes: 0 },
          latestWatchedAt: null,
        }),
      });
    }
    // Matched explicitly rather than left to the catch-all below, which answers 200 to anything — a
    // misspelled path would then pass this test suite and fail only against the real API.
    const deletedEntryId =
      method === "DELETE" ? path.match(/^\/watch-history\/entries\/([^/]+)$/)?.[1] : undefined;
    if (deletedEntryId) {
      mock.deleteWatchHistoryEntry?.(deletedEntryId);
      return route.fulfill({ status: 204, body: "" });
    }
    const datedEntryId =
      method === "PATCH" ? path.match(/^\/watch-history\/entries\/([^/]+)$/)?.[1] : undefined;
    if (datedEntryId) {
      mock.setWatchHistoryEntryTime?.(
        datedEntryId,
        (route.request().postDataJSON() as { watchedAt: string }).watchedAt,
      );
      return route.fulfill({ status: 204, body: "" });
    }

    // The engine is an optional dependency, so it is off unless a test says otherwise — the Convert and
    // Merge controls follow it.
    if (path === "/transcode/availability") {
      return route.fulfill({ json: { available: mock.transcodeAvailable ?? false } });
    }
    if (path === "/transcode/languages") {
      return route.fulfill({ json: mock.transcodeLanguages ?? ["eng", "ger", "rus", "ukr"] });
    }
    if (path === "/transcode" && method === "POST") {
      return route.fulfill({ status: 201, json: { id: "job-1" } });
    }
    // Extraction is its own route: it shares no field with a conversion, so the two request shapes never
    // meet on the wire either.
    if (path === "/transcode/extract" && method === "POST") {
      return route.fulfill({ status: 201, json: { id: "job-2" } });
    }
    if (path === "/library/backfill-media" && method === "POST") {
      return route.fulfill({
        json: mock.mediaBackfill ?? { itemsRefreshed: 0, remaining: 0, sidecarsFilled: 0 },
      });
    }

    if (path === "/metadata/search") return route.fulfill({ json: mock.metadataSearch ?? [] });

    // The title preview: /metadata/{provider}/{id}. An id the mock does not know is a 404, as it is on
    // the server — the dialog has to say so rather than hang.
    const previewId = path.match(/^\/metadata\/[^/]+\/([^/]+)$/)?.[1];
    if (previewId) {
      const preview = mock.titlePreview?.[previewId];
      return preview
        ? route.fulfill({ json: preview })
        : route.fulfill({ status: 404, json: { error: "not found" } });
    }
    if (/^\/ingest\/[^/]+\/search$/.test(path)) return route.fulfill({ json: mock.metadataSearch ?? [] });
    if (/^\/ingest\/[^/]+\/match$/.test(path)) return route.fulfill({ json: null });
    if (/^\/ingest\/[^/]+\/retarget$/.test(path)) return route.fulfill({ status: 202, json: null });
    if (/^\/library\/[^/]+\/remap$/.test(path)) return route.fulfill({ json: { id: mock.remapTargetId ?? "remapped" } });

    // Episode/season delete answers with what it pruned, so the UI knows when the series page is gone.
    if (method === "DELETE" && /^\/library\/(episodes|seasons)\/[^/]+$/.test(path)) {
      return route.fulfill({
        json: mock.childDelete ?? { seasonRemoved: false, seriesRemoved: false },
      });
    }

    const detailId = path.match(/^\/library\/([^/]+)$/)?.[1];
    if (detailId && mock.detail?.[detailId]) return route.fulfill({ json: mock.detail[detailId] });

    const episodesSeriesId = path.match(/^\/library\/([^/]+)\/episodes$/)?.[1];
    if (episodesSeriesId) return route.fulfill({ json: mock.episodes?.[episodesSeriesId] ?? [] });

    // Anything else the shell touches (downloads, ingest, catalogs for the ops strip) → empty.
    return route.fulfill({ json: [] });
  });
}

export const aMovie = (id: string, title: string) => ({
  id,
  publicId: id,
  catalogId: "c1",
  kind: "Movie",
  title,
  year: 2016,
  posterUrl: null,
  userData: null,
});

export const aSeries = (id: string, title: string) => ({
  id,
  publicId: id,
  catalogId: "c1",
  kind: "Series",
  title,
  year: 2022,
  posterUrl: null,
  userData: null,
});

export const aCatalog = (
  id: string,
  name: string,
  type: "Movie" | "Series" | "Anime",
  online = true,
  overrides: Record<string, unknown> = {},
) => ({
  id,
  name,
  type,
  root: `/media/${id}`,
  mountLabel: "media",
  mountRelativePath: id,
  namingTemplate: "{Title} ({Year})",
  defaultKeepSeeding: false,
  metadataLanguage: null,
  freeBytes: 1_000_000,
  online,
  unanchored: false,
  createdAt: "2026-07-12T00:00:00Z",
  updatedAt: "2026-07-12T00:00:00Z",
  ...overrides,
});

export const movieDetail = (id: string, title: string, tmdbId: string | null = null) => ({
  id,
  publicId: id,
  tmdbId,
  catalogId: "c1",
  kind: "Movie",
  title,
  originalTitle: null,
  year: 2016,
  overview: "An overview.",
  tagline: null,
  genres: ["Sci-fi"],
  officialRating: null,
  communityRating: 8.0,
  runtimeTicks: 70_560_000_000,
  indexNumber: null,
  indexNumberEnd: null,
  parentIndexNumber: null,
  posterUrl: null,
  backdropUrl: null,
  logoUrl: null,
  libraryPath: null,
  userData: null,
  mediaSources: [],
  seasons: null,
  networks: null,
  status: null,
  voteCount: null,
  seasonCount: null,
  episodeCount: null,
  collectionName: null,
  homepage: null,
  imdbId: null,
  trailerUrl: null,
  cast: [],
  directors: [],
  creators: [],
  studios: [],
  keywords: [],
});

// What `GET /metadata/{provider}/{id}` answers for a title the instance does not hold.
export const aTitlePreview = (
  providerId: string,
  title: string,
  overrides: Record<string, unknown> = {},
) => ({
  provider: "tmdb",
  providerId,
  kind: "Movie",
  title,
  originalTitle: null,
  year: 2010,
  overview: "A thief who steals corporate secrets through dream-sharing technology.",
  tagline: null,
  genres: ["Science Fiction", "Action"],
  posterUrl: null,
  backdropUrl: null,
  officialRating: "PG-13",
  communityRating: 8.4,
  voteCount: 34000,
  runtimeTicks: 88_800_000_000,
  status: "Released",
  seasonCount: null,
  episodeCount: null,
  directors: ["Christopher Nolan"],
  creators: [],
  cast: [{ provider: "tmdb", providerId: "6193", name: "Leonardo DiCaprio", character: "Cobb", profileUrl: null }],
  trailerUrl: null,
  imdbId: null,
  homepage: null,
  inLibrary: false,
  mediaItemId: null,
  ...overrides,
});

export const seriesDetail = (id: string, title: string, tmdbId: string | null = null) => ({
  ...movieDetail(id, title, tmdbId),
  kind: "Series",
  runtimeTicks: null,
  seasonCount: 1,
  episodeCount: 1,
  seasons: [],
  networks: [],
  directors: [],
  creators: [],
});

// One entry of a series detail's season rollup. `episodeCount` 0 is a real state: a season whose episodes
// were all deleted but which still holds extras is deliberately kept by the API.
export const aSeason = (id: string, seasonNumber: number, episodeCount: number) => ({
  id,
  publicId: id,
  seasonNumber,
  title: `Season ${seasonNumber}`,
  episodeCount,
  userData: null,
});

// `episodeNumberEnd` is set only for a file that holds a consecutive range (a "double episode").
export const anEpisode = (
  id: string,
  seasonNumber: number,
  episodeNumber: number,
  title: string,
  episodeNumberEnd: number | null = null,
) => ({
  id,
  publicId: id,
  seriesTmdbId: "123",
  seasonId: `season-${seasonNumber}`,
  seasonNumber,
  episodeNumber,
  episodeNumberEnd,
  title,
  overview: null,
  runtimeTicks: 2_400_000_000,
  posterUrl: null,
  userData: null,
});
