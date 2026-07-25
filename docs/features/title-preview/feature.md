# Title Preview

Created: 2026-07-25
Updated: 2026-07-25

A dialog that says what a title *is* — overview, facts, cast, trailer — for titles
the instance does not hold: the discoveries in the recommendation feed, the rows on
the release calendar and in the tracked drawer, and the TMDb search results in "Add a
title". Before it, an unfamiliar name on those surfaces could only be tracked blind or
looked up on TMDb in another tab.

Acquisition is not part of it, as everywhere else: the actions are Track/remind, Not
interested, and — when the title turns out to be held after all — a link to its library
page. See [Watchlist and discovery](../watchlist-and-discovery.md) and
[Recommendation providers](../recommendation-providers/feature.md).

## The dialog

A centered `Dialog` (`max-w-2xl`, `max-h-[85vh]`, the banner and body scrolling
together), not a route: no URL state and no deep link, dismissed by Escape or the
backdrop like every other dialog in the app.

Top to bottom:

- A **backdrop band** with a downward scrim, the **poster** overlapping it, and the
  title. With no backdrop the band is a plain tinted strip rather than a hole.
- **Facts**: year · runtime · genres, then the age-rating badge, the star rating with
  its vote count, and — for a series — the production status.
- **Directed by** / **Created by**, capped at three names.
- **Tagline** and **overview**.
- **Top cast** as a scrolling strip of up to 12 headshots with character names. The
  names are **not** links: `/people/{provider}-{id}` resolves against local `Person`
  rows, and a title nobody holds has none.
- **Actions**: `Track / remind me` (the shared `TrackTitleControl`, which lights up
  when the title is already on the calendar — matched on kind as well as provider id,
  since the two id spaces overlap), `Trailer`, `IMDb`, and `Not interested` on the
  recommendation surfaces. A held title leads with **Open in library**.

The dialog opens on what the calling card already knows — poster, title, year — and
fills in the rest when the request lands; only the parts that need it show skeletons.
A failed request shows the retry state without losing those known facts.

## Where it opens from

| Surface | What opens it |
| --- | --- |
| Recommendations (`/recommendations` and the home row) | the poster of a **discovery**; a held title keeps its link to the detail page |
| Tracked titles drawer | the poster/title area of a row, which closes the drawer (the bell, refresh and stop-tracking buttons keep their own hit areas) |
| Release calendar, day dialog | the row body; the bell beside it still opens the reminder dialog |
| "Add a title" search results | the result row; its `Track` button is unchanged |

Two rules keep it dismissable, both learned the hard way:

- It is rendered as a **sibling** of the surface that opens it, never inside that
  surface's root. Nested there, Base UI treats it as the parent overlay's own popup and
  an outside click never reaches it.
- **One modal overlay at a time.** The tracked drawer and the calendar's day dialog both
  close as they hand off to the preview; two focus traps side by side send Escape to the
  overlay underneath. The reminder dialog the preview hosts is the exception — it is a
  genuine child of the preview, which Base UI coordinates.

Its close button carries its own scrim, because a bare glyph disappears against a bright
backdrop.

## API

```http
GET /api/metadata/{provider}/{id}?kind=Movie|Series   →  TitlePreviewDto
```

Authenticated like the rest of `/api/metadata`, read-only. `kind` is required rather
than inferred: TMDb's movie and tv id spaces overlap, so probing one first would
happily return an unrelated title whenever the ids collide. An unknown provider is
`400`, a title the provider does not know is `404`.

`TitlePreviewDto` carries `provider`, `providerId`, `kind`, `title`, `originalTitle`,
`year`, `overview`, `tagline`, `genres[]`, `posterUrl`, `backdropUrl`,
`officialRating`, `communityRating`, `voteCount`, `runtimeTicks`, `status`,
`seasonCount`, `episodeCount`, `directors[]`, `creators[]`, `cast[]`, `trailerUrl`,
`imdbId`, `homepage`, `inLibrary`, `mediaItemId`. Names and types match
`LibraryDetailDto` wherever they overlap, so the web layer formats a preview with the
helpers it formats a detail page with.

`CalendarEventDto` carries `provider`/`providerId` for the same reason: a calendar row
identifies its title only by a local tracked-title id, which the preview cannot use.

## How it is served

- **A held title costs no request.** The identity lookup — published, top-level, kind
  matching, the same check the watchlist links titles with — finds the `MediaItem`, and
  the answer is projected from the library's own detail read. A preview never states
  anything different from the page it links to.
- **Otherwise one TMDb request**, through `IMetadataProvider.FetchAsync` for the primary
  configured metadata language. Its
  `append_to_response=credits,external_ids,videos,release_dates|content_ratings,keywords`
  already carries everything above; poster and backdrop come from the payload's own
  `poster_path`/`backdrop_path`, so there is no second `/images` call (and hence no title
  logo).
- **Cached** in `TmdbTitleDetailCache`, keyed `(Kind, TmdbId, Language)`: the raw payload
  plus `FetchedAt`, a 7-day TTL enforced on read, shared across users — the row says what
  TMDb says about a public title and records nobody's interest in it. The raw document is
  cached rather than a projection, so changing what the readers derive costs no refetch.
  When TMDb cannot be reached, a stale row still answers; a title it does not know is not
  cached as a negative.

`TmdbPayload` is the single reader of that document: the localized columns
(`MapDetails`, used by the provider itself) and the derived facts (`Parse` — crew,
brands, keywords, external ids, trailer, artwork, and the credits→cast projection the
preview needs). The library detail page reads the same payload through it, so the two
surfaces cannot drift.

## Not included

No URL addressability, no similar-titles rail, no keywords, no studios/networks, no
title logo, and no preview for library browsing — a held title has a full detail page
with playback, versions, episodes and admin controls, and the preview never imitates it.

## Testing Expectations

- `TitlePreviewServiceTests` — a held title answered from the library with no provider
  call, an unheld one projected from the payload (movie and series), cast from the
  payload credits, cache hit costing no request, stale row refreshed in place, stale
  payload served through an outage, unknown title → no preview, kind as part of the
  identity, and an unpublished item previewed as a discovery.
- `TmdbPayloadTests` — malformed payloads yielding no facts, the billed-cast cap and
  order, credits missing an id or a name skipped, artwork paths made absolute, networks
  only for series, and the kind deciding which title/date fields are read.
- `WatchlistServiceTests` — calendar events carrying the provider identity.
- `e2e/title-preview.spec.ts` — opening from a discovery poster and from the tracked
  drawer, the facts it states, tracking offered but never playback, dismissal closing
  the dialog, a held title linking to its page, the error state keeping the known
  facts, the preview opening over the search dialog without replacing it, a tracked
  series not being mistaken for the movie sharing its TMDb id, and the preview opened
  over the drawer being dismissable by its close button, Escape and an outside click.
