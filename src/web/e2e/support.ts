import type { Page } from "@playwright/test";

// Mocks the same-origin BFF (`/api/auth/session`, `/api/proxy/api/**`) so the UI can be driven without
// Hosty Core. Each test passes only the surfaces it cares about; everything else returns an empty list.

const session = (role: "admin" | "user") => ({
  userId: "u1",
  email: "tester@example.com",
  displayName: "Tester",
  role,
});

function userData(overrides: Record<string, unknown> = {}) {
  return {
    key: "k",
    playbackPositionTicks: 0,
    playCount: 0,
    isFavorite: false,
    played: false,
    playedPercentage: null,
    lastPlayedDate: null,
    unplayedItemCount: null,
    ...overrides,
  };
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
  catalogs?: unknown[];
  downloads?: unknown[];
  ingest?: unknown[];
  vpn?: unknown;
  metadataSearch?: unknown[];
  remapTargetId?: string; // id returned by POST /library/{id}/remap
  releaseCalendar?: unknown[]; // GET /watchlist/calendar
  watchHistoryCalendar?: unknown; // GET /watch-history/calendar (an envelope, not a list)
  watchHistoryUndated?: unknown; // GET /watch-history/calendar/undated ({ entries, total })
  recommendations?: unknown; // GET /recommendations ({ items, sources, selectedSources })
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
    if (path === "/torrents") return route.fulfill({ json: mock.downloads ?? [] });
    if (path === "/ingest") return route.fulfill({ json: mock.ingest ?? [] });
    if (path === "/vpn") return route.fulfill({ json: mock.vpn ?? null });
    if (path.endsWith("/played")) return route.fulfill({ json: userData({ played: method === "POST" }) });
    if (path.endsWith("/favorite")) return route.fulfill({ json: userData({ isFavorite: method === "POST" }) });

    if (path === "/watchlist/calendar") return route.fulfill({ json: mock.releaseCalendar ?? [] });
    // An envelope rather than a list, so it cannot fall through to the empty-array catch-all.
    if (path === "/recommendations") {
      return route.fulfill({
        json: mock.recommendations ?? { items: [], sources: [], selectedSources: [] },
      });
    }
    if (path === "/recommendations/hide" || path === "/recommendations/sources") {
      return route.fulfill({ status: 204, body: "" });
    }

    if (path === "/watch-history/calendar/undated") {
      return route.fulfill({ json: mock.watchHistoryUndated ?? { entries: [], total: 0 } });
    }
    if (path === "/watch-history/calendar") {
      return route.fulfill({
        json: mock.watchHistoryCalendar ?? { events: [], undated: { movies: 0, episodes: 0 }, latestWatchedAt: null },
      });
    }

    if (path === "/metadata/search") return route.fulfill({ json: mock.metadataSearch ?? [] });
    if (/^\/ingest\/[^/]+\/search$/.test(path)) return route.fulfill({ json: mock.metadataSearch ?? [] });
    if (/^\/ingest\/[^/]+\/match$/.test(path)) return route.fulfill({ json: null });
    if (/^\/library\/[^/]+\/remap$/.test(path)) return route.fulfill({ json: { id: mock.remapTargetId ?? "remapped" } });

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
) => ({
  id,
  name,
  type,
  root: `/media/${id}`,
  namingTemplate: "{Title} ({Year})",
  defaultKeepSeeding: false,
  metadataLanguage: null,
  freeBytes: 1_000_000,
  online,
  createdAt: "2026-07-12T00:00:00Z",
  updatedAt: "2026-07-12T00:00:00Z",
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
  seasonNumber,
  episodeNumber,
  episodeNumberEnd,
  title,
  overview: null,
  runtimeTicks: 2_400_000_000,
  posterUrl: null,
  userData: null,
});
