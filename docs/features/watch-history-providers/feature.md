# Watch History

Created: 2026-08-05
Updated: 2026-09-11

## Per-play history

`PlaybackHistoryEntry` is the local source of truth for a user's known plays.
Each entry belongs to one app user and media item. `UserItemData` carries the
aggregate watched flag, play count, last-watched time, favorite, rating, and
resume position used by the library and clients.

`WatchedAt` is an exact instant for an observed or explicitly dated viewing, and
null for a timeless "watched, time unknown" mark. `Origin` preserves whether the
entry came from observed playback, a manual action, an import, or legacy
aggregate reconstruction. Imported entries remain ordinary local history.

The [calendar](../watch-history-calendar/feature.md) and
[recommendations](../recommendation-providers/feature.md) read local history.
Recording and editing a play requires no external account or metadata match.

## Recording and editing

`WatchHistoryRecorder` stages entries in the same database transaction as the
corresponding aggregate changes. Native and Jellyfin playback share
`UserDataService`, so both apply the same rules.

- A playback session crossing 90% of the runtime records one exact play. A rewind
  below the threshold followed by another crossing in that session does not
  record another. A distinct session can record a rewatch. A client without a
  session id falls back to the aggregate watched flag.
- A manual watched toggle creates one timeless mark only when no history exists.
  Repeating the toggle does not manufacture additional entries.
- Unwatching removes local manual or legacy timeless marks. It preserves dated
  plays, imported history, and the existing play count.
- [Logging a watch](../watch-history-manual-entries/feature.md) creates a dated
  manual entry. Separate logs remain separate plays, even at the same instant.
- Correcting an entry's time updates the existing entry and its last-watched
  projection without changing the play count.
- [Deleting an entry](../watch-history-deletion/feature.md) removes that play,
  reprojects its aggregates, and reopens its recording session's completion gate.

Favorites and star ratings are local user state. They feed the library,
[recommendations](../recommendation-providers/feature.md), and client views.

## API and data boundaries

The authenticated `/api/watch-history` routes expose the calendar, undated
entries, timestamp edits, and entry deletion. Every route resolves the caller's
user identity; another user's entry is indistinguishable from an unknown id.
Native discovery also exposes the caller's local calendar and undated entries.

The database keeps the session uniqueness constraint and user/date indexes used
by history queries. Permanent item deletion cascades to its history; library
removal can retain a tombstone while user history still refers to it.

## Testing Expectations

- `WatchHistoryRecorderTests` and playback service tests cover session gating,
  rewatches, timeless marks, manual logs, unidentified items, season expansion,
  resume state, and unwatch behavior.
- `WatchHistoryEntryServiceTests` cover timestamp corrections, user isolation,
  deletion/reprojection, reopening the correct session, and tombstone cleanup.
- `WatchHistorySchemaTests` cover session uniqueness, distinct plays at the same
  instant, aggregate revisions, and item-deletion cascades.
- `LocalWatchHistoryMigrationTests` cover preservation of local and imported
  history, favorites, ratings, aggregates, and sessions during schema upgrades.
- `WatchHistoryCalendarServiceTests` and `WatchHistoryEndpointMappingTests`
  cover calendar projection and local mutation response semantics.
