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
  role?: "admin" | "user" | null; // null → unauthenticated (401)
  library?: unknown[] | { status: number };
  recent?: unknown[];
  resume?: unknown[];
  nextup?: unknown[];
  detail?: Record<string, unknown>;
}

export async function setupApp(page: Page, mock: AppMock = {}): Promise<void> {
  const role = mock.role === undefined ? "admin" : mock.role;

  await page.route("**/api/auth/session", (route) =>
    role
      ? route.fulfill({ json: session(role) })
      : route.fulfill({ status: 401, json: { error: "unauthenticated" } }),
  );

  await page.route("**/api/proxy/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname.replace("/api/proxy/api", "");
    const method = route.request().method();

    if (path === "/library") {
      if (mock.library && !Array.isArray(mock.library)) {
        return route.fulfill({ status: mock.library.status, json: { error: "boom" } });
      }
      return route.fulfill({ json: mock.library ?? [] });
    }
    if (path === "/library/recent") return route.fulfill({ json: mock.recent ?? [] });
    if (path === "/library/resume") return route.fulfill({ json: mock.resume ?? [] });
    if (path === "/library/nextup") return route.fulfill({ json: mock.nextup ?? [] });
    if (path.endsWith("/played")) return route.fulfill({ json: userData({ played: method === "POST" }) });
    if (path.endsWith("/favorite")) return route.fulfill({ json: userData({ isFavorite: method === "POST" }) });

    const detailId = path.match(/^\/library\/([^/]+)$/)?.[1];
    if (detailId && mock.detail?.[detailId]) return route.fulfill({ json: mock.detail[detailId] });
    if (/^\/library\/[^/]+\/episodes$/.test(path)) return route.fulfill({ json: [] });

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

export const movieDetail = (id: string, title: string) => ({
  id,
  publicId: id,
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
  parentIndexNumber: null,
  posterUrl: null,
  backdropUrl: null,
  libraryPath: null,
  userData: null,
  mediaSources: [],
  seasons: null,
});
