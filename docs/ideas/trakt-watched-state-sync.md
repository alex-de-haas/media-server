# Watched-History Providers: Trakt

Status: Promoted
Created: 2026-07-21
Updated: 2026-07-24

## Motivation

Media Server stores watched flags, aggregate play counts, resume positions,
favorites, and a shared last-played timestamp in local SQLite. This works for
Infuse and the web UI, but watched state cannot follow a user to other applications
or a new Media Server installation.

An external watched-history provider can make that state portable while Media
Server remains offline-first. Trakt is the first implementation of a general
provider boundary, not a Trakt-specific branch inside playback code.

## Current Constraints

- `UserItemData` is aggregate-only and contains no individual play records.
- Media Server marks watched at 90% and increments `PlayCount` only when `Played`
  changes from false to true.
- Explicit unwatch clears watched/resume but retains historical `PlayCount`.
- `LastPlayedDate` also represents starts and progress, so it is not a trustworthy
  completion time.
- Jellyfin progress requests can contain `PlaySessionId`, but it is currently
  ignored. PlayedItems can contain client-supplied `DatePlayed`.
- Real playback and manual watched already use separate route families;
  observation mainly needs to verify `PlaySessionId` echo and whether Infuse emits
  a duplicate PlayedItems POST after natural completion.
- Current threshold logic accepts a first report already above 90% and can count
  the same session twice after a rewind below 90%.
- Actual Infuse route/field behavior for playback, manual watched, unwatch, and
  rewatch has not yet been observed in enough detail.
- Movies normally have a TMDb movie ID. Episodes use a parent TMDb series ID plus
  canonical season/episode coordinates.
- Different local catalogs can publish multiple items with the same TMDb identity.
- Trakt supports exact and `unknown` watched additions, stores multiple plays, and
  can remove individual history entries by remote history ID.
- The Trakt add response does not directly provide the individual history ID. It is
  not yet verified whether a later history read preserves `unknown` distinctly.
- Every Hosty app user needs an independent Trakt account, while the instance
  operator owns one Trakt API application and its client credentials.
- Future watched-history providers may have different authorization, identifier,
  timestamp, read, and removal capabilities.

## Possible Approaches

### Approach A: Outbound Watched Marks Only

Export a local watched transition, but never read Trakt into Media Server.

Pros:

- Smallest conflict surface.
- Local state is always authoritative.

Cons:

- Existing Trakt history cannot restore a new installation.
- Watches created in another Trakt client remain invisible locally.

### Approach B: Automatic Bidirectional Sync

Periodically merge Trakt and local watched state.

Pros:

- Changes from other clients appear automatically.
- Trakt behaves like a continuously portable profile.

Cons:

- Remote exact history can repeatedly reverse an intentional local unwatch.
- Multiple providers could create hidden provider-to-provider replication.
- Conflict resolution is difficult with aggregate-only local storage.

### Approach C: Explicit Scoped Sync

Export normal local changes asynchronously, but import/reconcile only when the user
runs **Sync with Trakt** for selected catalogs. With reliable playback detection,
the operation reconciles full per-play history; legacy local-only watched state is
added once as `unknown` before Trakt history is projected locally.

Pros:

- User controls the only inbound/destructive operation.
- Allows full local history while keeping inbound changes user controlled.
- Preserves local-only watched items without fabricating historical timestamps.
- Scope selection can limit changes to chosen catalogs.

Cons:

- Local `PlayCount > 1` can collapse to one after only one safe unknown mark is
  exported and Trakt becomes aggregate-authoritative.
- Remote changes are not visible until the user runs Sync.
- Pre-migration aggregate counts remain lossy.

### Approach D: Full Per-Play Mirror

Add a local play-history table, import every Trakt play, and record every reliable
Infuse completion.

Pros:

- Preserves exact timestamps and repeated views.
- Makes play-count reconciliation explainable.

Cons:

- Requires a trustworthy completion/session signal from Infuse.
- Requires paginated full Trakt history during Sync.
- Adds substantially more persistence, matching, and idempotency complexity.

## Risks

- A newly added Trakt history ID may not be immediately visible. Owned-marker
  resolution needs a persisted before-set, delayed reads, and an unresolved state.
- Trakt does not deduplicate item plus watched time; an ambiguous response can
  produce duplicate plays on retry.
- Infuse might emit PlayedItems after natural completion. Without active-session
  correlation this creates an artificial unknown play.
- Missing `PlaySessionId` requires a server fallback session; a poor inactivity
  window can join or split playbacks.
- A long Sync can overwrite a locally completed episode unless state revisions are
  checked immediately before applying each row.
- Removing a Trakt media object rather than individual history IDs can erase all
  exact plays and is never a safe fallback.
- Applying one Trakt identity to every local duplicate can erase another edition's
  resume point; selecting one by query order is arbitrary.
- Credentials must live outside the backed-up SQLite file. Resolved by the Hosty
  Core app secrets store (Core 0.60.0), which holds them beside `state.json`
  rather than in the app's `data/` directory, so Media Server needs no
  encryption key of its own.
- A generic provider contract can leak Trakt-specific OAuth, `unknown`, or history
  ID concepts unless capabilities and adapter-owned DTOs are enforced.

## Open Questions

- **Question:** Does Infuse echo `PlaySessionId`, and which correlation window is
  safe if it does not?
  **Current answer:** The server already returns a GUID from PlaybackInfo, but does
  not currently log or consume the value in progress reports.
  **Recommendation:** Confirm the echo. Otherwise open a server-derived user/item
  session on Playing and measure a deterministic inactivity window in Phase 0.

- **Question:** Should full local per-play history be added now?
  **Current answer:** It is a required part of this feature if Infuse supplies
  reliable per-play evidence. It is not being rejected or moved to an unrelated
  future feature.
  **Recommendation:** Gate it on the diagnostic phase. On successful validation,
  add `PlaybackHistoryEntry` in this plan and make Sync retrieve paginated full
  Trakt history. If validation fails, keep the plan Draft and return to the user
  instead of silently shipping aggregate-only behavior.

- **Question:** Can Media Server resolve the exact Trakt ID of its own unknown
  addition reliably?
  **Current answer:** The add response omits it, but a serialized before/after ID
  difference can identify it without relying on sentinel round-trip.
  **Recommendation:** Persist the before-set in the outbox, retry delayed reads,
  and store only a unique new ID on the local history entry. Never delete or repost
  after an ambiguous result.

- **Question:** Which item identity does Infuse report for a multi-episode file?
  **Current answer:** It has not yet been observed.
  **Recommendation:** Add the case to Phase 0 and use the trace to lock history
  expansion before approval.

- **Question:** How should inbound Sync handle multiple local copies with one Trakt
  identity?
  **Current answer:** Trakt considers them one work, but local resume/watched state
  can legitimately differ by edition and catalog.
  **Recommendation:** Keep outbound copies independent. During inbound Sync, treat
  multiple candidates inside the selected scope as `AmbiguousLocalIdentity` and
  change none. Selecting one catalog can resolve cross-catalog collisions.

## Current Recommendation

Proceed with Approach C for user-controlled synchronization and include Approach D
storage semantics when the Infuse observation succeeds:

- expose Trakt through provider-neutral contracts and capabilities;
- ship only Trakt and allow only one active provider per user initially;
- let the operator configure one Trakt app in Hosty Media Server settings;
- let each user connect their own Trakt account in Settings near Infuse Access;
- keep local actions offline-first through a transactional outbox;
- offer explicit catalog-scoped **Sync with Trakt**, with no automatic inbound
  polling;
- when Infuse proves actual completion, add `PlaybackHistoryEntry`, export reliable
  exact plays, fetch full paginated Trakt history, and project it back locally;
- define exact playback as the first below-to-at-least-90% crossing per durable
  echoed/fallback session; accept seeking, reject first-report-at-90%, and count
  only once across rewind/repeated calls;
- correlate a PlayedItems POST inside the session window as a duplicate and treat
  it as manual only outside that window;
- export one unknown mark for a currently watched legacy/manual item missing from
  Trakt;
- never fabricate multiple historical plays from local `PlayCount`;
- resolve and store the remote ID of Media Server-created timeless entries through
  read-before/write/read-after diff;
- on manual unwatch, remove only those owned timeless entries and preserve external
  timeless plus exact plays;
- protect long Sync application with per-row state revisions and preserve legacy
  `PlayCount` for unwatched rows with no imported history;
- batch season/series reads and mutations rather than looping per episode;
- decide exact timestamps and rewatch behavior only after inspecting Infuse logs;
- if the experiment fails, keep the plan Draft and return for a product decision;
- keep scrobble, resume sync, ratings, and lists outside the current scope.

## Links

- [Implementation planning](../planning/trakt-watched-state-sync.md)
- [Watch-history calendar plan](../features/watch-history-calendar/plan.md)
- [Jellyfin compatibility](../features/jellyfin-compatibility.md)
- [Storage and data](../features/storage-and-data.md)
- [Security](../features/security.md)
- [Trakt create an app](https://docs.trakt.tv/docs/create-an-app)
- [Trakt OAuth authentication](https://docs.trakt.tv/docs/authentication-oauth)
- [Trakt watched-history addition](https://docs.trakt.tv/reference/postsynchistoryadd)
- [Trakt watched-history removal](https://docs.trakt.tv/reference/postsynchistoryremove)
- [Trakt watched-state retrieval](https://docs.trakt.tv/reference/getsyncwatched)
- [Trakt watched-history retrieval](https://docs.trakt.tv/reference/getsynchistoryget)
- [Trakt API rate limits](https://docs.trakt.tv/docs/rate-limiting)

## Notes

Trakt documentation was reviewed on 2026-07-21. External contracts and rate limits
must be rechecked immediately before implementation.
