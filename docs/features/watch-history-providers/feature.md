# Watched-History Providers: Trakt

Created: 2026-07-30
Updated: 2026-07-30

> **Development is wound down** — an operator decision taken on 2026-07-30, not
> a limitation derived from Trakt's published terms. The shipped code stays and
> keeps working for a connected account; nothing further is being built, and the
> remaining plans were retired rather than parked.
>
> What Trakt's connection limit actually says, verified against its announcement
> on both 2026-07-24 and 2026-07-30: a **free account may connect one Community
> App**, and VIP is required only for a second. Official Trakt clients do not
> count toward it. So a free account with its slot free can still connect this
> app; a free account whose slot is already spent — on Infuse's own Trakt
> integration, say — cannot add this one without VIP, and that is the wall the
> operator hit.
>
> The **per-play history** below is not affected by any of this. It is
> provider-neutral, it is what the [watch-history
> calendar](../watch-history-calendar/feature.md) and
> [recommendations](../recommendation-providers/feature.md) read, and it keeps
> being written whether or not any provider is connected.

## Per-play history

`PlaybackHistoryEntry` holds one row per known play, scoped to an app user and a
media item. This is the local source of truth; `UserItemData`'s aggregate
counters are projected from it.

- `WatchedAt` is an exact instant for observed or imported plays, and **null**
  for a timeless "watched, time unknown" mark — all a manual toggle or a
  pre-migration aggregate row can honestly claim.
- `Origin` records where it came from: `LocalPlayback`, `Manual`,
  `ProviderSync`, or `Legacy`.
- `IdentitySnapshot` freezes the provider-neutral identity as it was when the
  play happened, so outbound delivery can still describe an item that has since
  been rescanned, re-identified, or deleted.
- `ProviderKey` / `ProviderHistoryId` / `LinkStatus` record the link to a remote
  entry once one is resolved.

**One session yields one play.** `PlaybackSession`, keyed on the client's
`PlaySessionId`, gates completion: crossing the 90% threshold counts once, and
rewinding below it and crossing again does not count a second. This was a real
bug found by observation rather than by reasoning — one continuous session took
an episode from 0 to 3 plays — and the session gate is what fixes it. A client
that sends no session id falls back to the aggregate flag and can still inflate;
Infuse sends one on every report.

Genuine rewatches are kept as separate entries. At most one timeless entry
exists per user and item, because "watched, time unknown" says nothing more the
second time.

## Provider boundary

`IWatchHistoryProvider` and `IWatchHistoryProviderAuthorization` are resolved by
stable key through `WatchHistoryProviderRegistry`, with a capability descriptor
(`ExactTimestampWrites`, `TimelessWrites`, `FullHistoryReads`,
`IndividualEntryRemoval`, …) so callers ask what an adapter can do rather than
naming it. Trakt is the only adapter, and one connection may be active per user.

`WatchHistoryIdentityMapper` turns a local item into that neutral identity.
Episodes are addressed by their **series** id plus canonical season and episode
numbers, preferring `IdentitySeasonNumber`/`IdentityEpisodeNumber` over the
display numbering — a re-mapped release (anime absolute numbering) displays one
way and is identified another, and writing against the display numbers would
record viewings for the wrong episodes.

## Trakt adapter

- **Device Code OAuth**, per user. Tokens live in the Hosty Core secrets store,
  never in this app's database — so a database restore cannot resurrect stale
  credentials, and a backup carries no bearer token. Refresh rotates the refresh
  token and persists what comes back.
- A transient failure while refreshing keeps its kind: a Trakt outage must not
  be reported as `AuthenticationRequired` and send the user to reconnect an
  account that is still connected.
- Every request carries a `User-Agent`; without one Cloudflare answers 403.
- **`/sync/history/{type}/{id}` takes a Trakt id, slug, or IMDb id — never a
  TMDb id**, which it answers with `200` and an empty array. `TraktWorkIdResolver`
  prefers an IMDb id when the identity carries one (no lookup needed) and
  otherwise translates the TMDb id through `/search/tmdb/{id}`, caching the
  mapping for the process. A lookup that fails travels back as a failure rather
  than as an empty history: collapsing "I could not ask" into "there is nothing"
  is what made this bug invisible for weeks.
- Removal uses the **ids form only**, deleting exactly the entries this app
  created and recorded. Trakt's media-object removal would take every play of
  that item with it, including another client's.

## Outbound delivery

Local state changes never wait on Trakt. `UserDataService` writes the aggregate
row, the history entry, and the outbound intent in one transaction; a worker
performs the external call later.

`WatchHistoryDeliveryService` leases up to 20 events per 30-second tick, with
5-minute leases, 8 attempts, and exponential backoff. Failures are classified
retryable or terminal rather than retried blindly.

Ownership resolution is read-before / write / read-after: Trakt's add response
returns counts, not ids, so the remote id is found from the difference between
the two reads. An add whose id cannot be pinned down settles as `Unresolved` and
is **never** reposted or deleted remotely — guessing there means destroying
history this app did not create.

## Explicit sync

Sync is user-triggered, scoped by catalog and media kind, and always previewed.

- **Preview** is read-only. It classifies each item (`InSync`, `RemoteOnly`,
  `LocalOnly`, `LocalUnwatchedWithHistory`, `UnidentifiedLocally`,
  `AmbiguousLocalIdentity`), captures each row's state revision, and expires.
- **Apply** exports, re-reads, then projects — in that order. Projecting from a
  snapshot taken before the export would erase the very plays just sent.
- Apply refuses to run while **any** outbound work is undelivered. Otherwise
  unmarking an item and syncing before the removal is delivered would reimport
  the still-present remote mark and silently undo the unwatch.
- A row that changed since the preview, or that appeared after it, is set aside
  rather than overwritten. Anything set aside keeps all of its local state: a
  half-applied item is worse than an unapplied one.
- A local count of 5 with no recorded times exports as **one** timeless mark;
  five `unknown` entries would invent four viewings nobody claimed.

## Favorites

The favorite flag syncs through the same connection and is documented separately
in [Trakt favorites sync](../trakt-favorites-sync/feature.md).

## Settings surface

A **Watch history providers** card sits near Infuse Access: connection status
and account name, the Device OAuth code and verification URL, Connect /
Reconnect / Disconnect, `Last sync` beside `Last delivery` (a background
delivery can land without a sync ever running), and the Sync dialog with its
scope, counts, and set-aside reasons.

No response carries an access token, refresh token, device code, or
secrets-store key; a test pins that.

Disconnecting revokes the token best-effort, deletes the stored credential, and
**never touches local playback state** — disconnecting a provider is not
forgetting what you watched.

## Not built

These were on the plan and are not being built:

- **Grouped season/series delivery.** Each outbox event resolves one identity,
  so marking a season performs one read/mutation pair per episode.
  `GetHistoryAsync` already accepts an identity list, so grouping would be
  additive.
- **Directory reconciliation and structured telemetry** for the delivery worker.

## Not verified

Honest gaps, and now unclosable without a VIP account:

- **The removal path has never been confirmed end to end against a live
  account.** The id bug above meant no remote id was ever captured, so every
  unwatch completed having correctly removed nothing; the fix is covered by
  tests and its request shape was verified by hand against the live API, but the
  full watch → unwatch cycle was never re-run afterwards.
- Entries recorded before that fix remain `Unresolved` by design and will not
  resolve retroactively.
- Rate-limit and long-history pagination behavior is covered by tests against a
  stub, not by a live run.

## Testing Expectations

- `WatchHistoryRecorderTests`, `UserDataService` playback tests — the session
  gate, one-completion-per-session, timeless-vs-exact origins, and the
  benign concurrent-report race.
- `WatchHistoryIdentityMapperTests` — canonical over display numbering, series
  expansion, unresolvable identities reported rather than guessed.
- `TraktOAuthClientTests`, `TraktAuthorizationServiceTests` — device flow,
  token persistence before the account lookup, transient-vs-permanent refresh
  failures, poll-gate backoff.
- `TraktWatchHistoryProviderTests`, `TraktWorkIdResolverTests` — the id the
  per-work paths accept, IMDb preferred over a lookup, a failed lookup reported
  as a failure rather than an empty history, pagination, owned-only removal.
- `WatchHistoryDeliveryServiceTests` — leases, backoff, ownership resolution,
  crash-idempotency, and never reposting an unresolved add.
- `WatchHistorySyncPreviewServiceTests`, `WatchHistorySyncApplyServiceTests` —
  classification, export-then-project ordering, blocking on undelivered work,
  set-aside rows keeping their local state, and failed exports not being
  projected over.
- `WatchHistoryEndpointMappingTests` — failure-to-status mapping and that no
  response carries credential material.
