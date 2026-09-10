# Indexing Progress

Created: 2026-09-10
Updated: 2026-09-10

## Behavior

Web movie and expanded episode media cards, and Apple TV movie/episode Versions,
display background remux preparation beside each file. External Matroska audio
tracks display their own status beside the track. Optional dub preparation does
not block the video version or imply that another version cannot play.

| State | Presentation |
| --- | --- |
| `waiting` | Waiting for indexing; indeterminate indicator |
| `indexing` | Indexing with a percentage and progress bar |
| `saving` | Saving index; indeterminate indicator |
| `ready` | No preparation indicator |
| `failed` | Indexing could not finish |
| Absent or unknown | No preparation indicator; existing playback explanations apply |

The Apple TV playback notice uses the attempted version's live status. After its
index finishes, the notice invites the viewer to press Play again. Completion
does not start playback, switch versions, or claim support for a codec.

## Measurement and lifecycle

`MatroskaIndexer` reports the traversed file offset after each cluster. Percentage
uses this offset divided by source length: payloads are skipped, so this measures
file traversal rather than bytes physically read or playback duration. Intermediate
progress is monotonic within an attempt and capped at 99. Saving is a distinct
state; ready follows successful index storage. No completion-time estimate is shown.

`IndexingProgress` holds at most 512 attempt snapshots in memory, keyed by source
or external stream ID. Progress publication is limited to one changed percentage
per 500 milliseconds. Start, saving, completion, failure and cancellation
transitions publish immediately. A cancelled or changed-file attempt returns to
waiting; exceptions and files with no indexed tracks report failure. Subsequent
attempts reset progress. Snapshots are associated with source length and modification
time, so a replaced file does not inherit the previous file's progress.

Current index stamps determine ready state. Eligible files without an index or
active attempt report waiting; unavailable and non-indexable files have no status.
Ready/cancelled attempts leave the tracker, and retained failures are bounded by
oldest-revision eviction. A process restart discards transient state and rediscovers
pending work through the existing worker. Scheduling remains sequential, including
the existing startup delay, batches and idle interval. Pressing Play does not start
or prioritize an indexing job.

## API and SSE

The shared `MediaSourceDto` and external audio `MediaStreamDto` carry optional
`indexing: { state, percent, revision }`. Native item detail embeds the same DTOs
as the web library surface; the OpenAPI document and generated Swift client carry
the fields. Percentage is null outside active traversal.

Both existing authenticated routes publish `indexingChanged`: `/api/events`
(through the web BFF) and `/native/v1/events`. Its payload contains `itemId`,
`sourceId`, optional `streamId`, and `indexing`. Revisions order snapshots and
live updates, including retries. No filesystem path or credential is included.
Only published, non-removed items enter discovery, and event delivery rechecks
item visibility in case a title disappears during indexing.

The notifier bounds each subscriber's channel. A subscriber that falls behind
its buffer is disconnected, causing snapshot reconciliation after reconnect
instead of silently dropping a completion event. This behavior applies to the
shared stream's other events as well.

## Clients and recovery

Web uses its existing `RealtimeBridge`, storing progress in revisioned React Query
entries independently of detail fetches. Version/track indicators select the newer
snapshot or event. Reconnection invalidates library detail queries; foreground
query refresh also reconciles snapshots. A disconnected stream adds a reconnecting
label to the last known preparation state. There is no indexing polling timer or
connection per card.

MediaKit's `IndexingFeed` shares one authenticated SSE connection across subscribed
detail views, bounded to 512 remembered file states. It parses fragmented SSE
frames, ignores unrelated/unknown events, reconnects with backoff, and uses the
session's existing credential refresh flow. The last subscriber's cancellation
closes the connection. Title views subscribe while active and outside playback,
refresh detail on connection, and preserve selected versions. An older server
without indexing fields retains the existing playback notice.

## Testing Expectations

- xUnit coverage for traversal offsets, throttling, monotonic revisions, saving
  versus ready, failure/retry/cancellation, changed and missing sources, process
  restart, independent sidecars, and shared detail projection.
- SSE regression coverage for ownership metadata and bounded-subscriber overflow
  followed by successful reconnection.
- Web unit coverage for status presentation, malformed/unknown events, independent
  tracks and revision ordering. Browser scenarios exercise movie and episode
  cards through waiting, progress and saving without per-tick detail fetches,
  then recover a missed completion via reconnect. Inspect desktop/mobile layout.
- MediaKit coverage for fragmented/CRLF/multiline frames, malformed and oversized
  frames, optional generated DTO mapping, revision ordering, shared authenticated
  subscriptions and last-subscriber cancellation. Build tvOS without signing.
- Verify physical Apple TV focus, rendering and actual indexing completion through
  an authorized Hosty environment when available; mocked BFF/network tests do not
  substitute for that integration check.
