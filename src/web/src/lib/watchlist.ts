// Typed client for the release-tracking surface (`/api/watchlist` + `/api/reminders`), reached through
// the same-origin BFF proxy like the rest of the API. Dates are calendar dates ("yyyy-MM-dd").

import { apiFetch, apiJson } from "@/lib/api";

const BASE = "/api/proxy/api";

export type TrackedKind = "Movie" | "Series";
export type ReleaseType = "Premiere" | "Theatrical" | "Digital" | "EpisodeAir";
export type SeriesMonitorScope = "WholeShow" | "Seasons" | "FutureEpisodes";
/** Reminders-drawer pill state. */
export type ReminderState = "scheduled" | "recurring" | "released" | "pending";
/** Create-time resolution of a reminder. */
export type ReminderResolutionState = "scheduled" | "alreadyReleased" | "pending";

export interface NextRelease {
  type: ReleaseType;
  date: string;
  season: number | null;
  episode: number | null;
}

/** Owned-vs-aired projection for a library-linked series. */
export interface LibraryGap {
  ownedEpisodes: number;
  airedEpisodes: number;
  missingAired: number;
}

export interface Reminder {
  id: string;
  trackedTitleId: string;
  title: string;
  posterUrl: string | null;
  kind: TrackedKind;
  releaseType: ReleaseType;
  leadDays: number;
  /** "HH:mm" in the app timezone. */
  notifyAt: string;
  active: boolean;
  state: ReminderState;
  date: string | null;
}

export interface WatchlistItem {
  id: string;
  trackedTitleId: string;
  kind: TrackedKind;
  title: string;
  year: number | null;
  posterUrl: string | null;
  provider: string;
  providerId: string;
  productionStatus: string | null;
  inLibrary: boolean;
  libraryItemId: string | null;
  monitorScope: SeriesMonitorScope | null;
  monitoredSeasons: number[] | null;
  regionOverride: string | null;
  note: string | null;
  nextRelease: NextRelease | null;
  hasDates: boolean;
  libraryGap: LibraryGap | null;
  reminders: Reminder[];
}

export interface CalendarEvent {
  releaseId: string;
  entryId: string;
  trackedTitleId: string;
  kind: TrackedKind;
  title: string;
  posterUrl: string | null;
  type: ReleaseType;
  date: string;
  previousDate: string | null;
  season: number | null;
  episode: number | null;
  note: string | null;
  hasReminder: boolean;
  inLibrary: boolean;
}

export interface AddWatchlistInput {
  providerRef: { provider: string; id: string };
  kind: TrackedKind;
  monitorScope?: SeriesMonitorScope | null;
  monitoredSeasons?: number[] | null;
  regionOverride?: string | null;
  note?: string | null;
  /** Display hints from the search candidate so the row renders before the first sync. */
  title?: string | null;
  year?: number | null;
  posterUrl?: string | null;
}

export interface UpdateWatchlistInput {
  setMonitorScope?: boolean;
  monitorScope?: SeriesMonitorScope | null;
  monitoredSeasons?: number[] | null;
  setRegionOverride?: boolean;
  regionOverride?: string | null;
  setNote?: boolean;
  note?: string | null;
}

export interface CreateReminderInput {
  trackedTitleId?: string | null;
  providerRef?: { provider: string; id: string } | null;
  kind?: TrackedKind | null;
  releaseType: ReleaseType;
  leadDays: number;
  /** "HH:mm"; the backend defaults to 09:00 when omitted. */
  notifyAt?: string | null;
  title?: string | null;
  year?: number | null;
  posterUrl?: string | null;
}

export interface ReminderResolution {
  reminder: Reminder;
  state: ReminderResolutionState;
  date: string | null;
  /** Series context, e.g. "Already airing — up to S2E10 (Apr 5, 2026)." */
  detail: string | null;
}

async function send(path: string, method: string, body?: unknown): Promise<void> {
  await apiFetch(`${BASE}${path}`, {
    method,
    ...(body !== undefined
      ? { headers: { "content-type": "application/json" }, body: JSON.stringify(body) }
      : {}),
  });
}

export const watchlistApi = {
  list: () => apiJson<WatchlistItem[]>(`${BASE}/watchlist`),
  add: (input: AddWatchlistInput) =>
    apiJson<WatchlistItem>(`${BASE}/watchlist`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  update: (id: string, input: UpdateWatchlistInput) =>
    apiJson<WatchlistItem>(`${BASE}/watchlist/${id}`, {
      method: "PATCH",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  remove: (id: string) => send(`/watchlist/${id}`, "DELETE"),
  /** Dated release events for the user's tracked titles; from/to are "yyyy-MM-dd". */
  calendar: (from: string, to: string) =>
    apiJson<CalendarEvent[]>(`${BASE}/watchlist/calendar?from=${from}&to=${to}`),
  refresh: (id: string) => send(`/watchlist/${id}/refresh`, "POST"),

  listReminders: () => apiJson<Reminder[]>(`${BASE}/reminders`),
  createReminder: (input: CreateReminderInput) =>
    apiJson<ReminderResolution>(`${BASE}/reminders`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  updateReminder: (id: string, input: { leadDays?: number; notifyAt?: string; active?: boolean }) =>
    apiJson<Reminder>(`${BASE}/reminders/${id}`, {
      method: "PATCH",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input),
    }),
  deleteReminder: (id: string) => send(`/reminders/${id}`, "DELETE"),
};
