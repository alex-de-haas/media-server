# Apple Playback Buffering

Created: 2026-09-22
Updated: 2026-09-22

## Behavior

Settings → Playback exposes **Enable cache**. When enabled, **Cache storage**
offers **Memory** and **Disk**. Disabled uses AVPlayer's native network loading
and buffering, without the application-owned cache or loader recovery. Direct
play bypasses the loader regardless of these settings.

Memory uses the 128 MiB window with an 8 MiB tail. Disk uses 8 MiB block files under
`Library/Caches/MediaServerPlayback/session-<uuid>`. The server, virtual MP4, codecs
and AVKit presentation are unchanged.

Memory is the default on fresh installs and for saved preferences without a
storage choice. The existing enable flag is preserved, including an explicit
choice to disable the old loader. Storage selection survives disabling the cache
and relaunching the app. Unknown future storage names fall back to memory without
resetting other preferences. Settings apply on the next playback session and
remain consistent across track changes and item recovery.

In disk mode, once AVPlayer reports duration, the loader estimates bytes per second from the
virtual resource's total length divided by duration. It targets approximately
300 seconds ahead and retains approximately 60 seconds behind its inferred reader
position. The combined budget is capped at 6 GiB and reduced to fit currently
available space, reserving 2 GiB plus block-boundary slack at creation. Space is
checked again at write intervals of approximately 32 MiB. Before duration is
known, the target is 128 MiB with an 8 MiB retention tail.

These seconds are **byte estimates**, not verified playable intervals. Variable
bitrate, initialization tables, interleaving and sidecar audio in a separate
virtual-file region affect the estimate. The loader retains existing reader
tracking rather than parsing a time-to-sample map. It starts delivering as data
arrives; it does not wait for the full cache to fill. Read-ahead grows independently
of the existing AVPlayer delivery throttle.

Fills fetch at most 32 MiB. Disk reads deliver at most 1 MiB per request per queue
turn, yielding between turns to service cancellation and other callbacks. At most
three separate requests are active, each bounded to 8 MiB. All disk I/O runs on
the serial loader queue, off the main actor. There is no movie-sized `Data` or
memory mapping; disk capacity is independent of the player's own memory use.

Short backward/forward seeks reuse bytes still held. Distant seeks reset the
window with the original small preroll, not an entire minute of download before
the target. The large retention tail is separate from the small reader/seek
heuristics. Initialization probes and outlying reads still use separate HTTP
requests; the cache does not retain arbitrary disjoint ranges.

The session owns its files, removes evicted blocks and cleans up on stop. The
first disk-cache use after process launch removes abandoned session directories.
Item recovery reuses the same cache; a track change creates a new loader and cache.
Responses must match requested ranges and the HEAD response's ETag when present;
`If-Range` prevents silently mixing changed representations.

Creation, space, read, write or eviction failures discard the disk window and
continue through a 128 MiB memory window. Pending requests resume at their current
offsets. Obsolete separate-request callbacks cannot interfere with replacement
fetches. Discarded RAM chunks release their payloads immediately.

## Device experiment and diagnostics

Client version **0.15.0** includes this experiment. Enable playback diagnostics to
compare the three cache settings on the same film. The common compact column is
present in every mode: player buffer estimates, stalls/recoveries/errors, dropped
frames, startup time, resident RAM, network GET count/frequency, aggregate bytes
per GET, received bytes and recent/peak throughput. Native access logs supply
network data without a cache; the loader supplies it in memory/disk mode. The
[Apple client diagnostics](../apple-client/feature.md#playback-diagnostics)
document source semantics, unknown readings and replacement behavior.

The cache-only second column shows:

- Memory, disk or RAM fallback mode, logical usage/target, ahead/behind bytes and
  approximate ahead seconds.
- Cache-delivered bytes, separate from the common network traffic total.
- Pending requests, requests whose next byte is cached, oldest request age,
  open-ended delivery throttling, readers/spread, resets and separate-request counts.
- Maximum disk read/write durations and failure count when disk has been used.
- Cache snapshots at the last stall, recovery and minimum player-buffer estimate,
  including cache mode/ahead bytes, failures, pending/cached counts and throttle.

The common column retains the last stall/recovery position and player-buffer
estimate. The buffer chart merges touching/overlapping loaded ranges, displays
its scale and does not equate a zero estimate with a visible freeze. The overlay
uses two 600-point columns, 18-point rows and a 36-point chart to keep the maximum
layout compact; cache-specific rows disappear on the native path.

An old open-ended request is not necessarily stuck, and a cached next byte does
not prove that every needed sample is available. These figures support diagnosis;
they do not classify the cause automatically. A photograph after a freeze can
capture its retained snapshot. The [plan](plan.md) tracks the outstanding user
test on the affected Apple TV; macOS tests and a tvOS build do not establish that
the original playback interruption is fixed.

## Testing Expectations

- Common native/loader network counter selection, event aggregation, unavailable
  counters, rate resets, retired-item notifications and contiguous loaded ranges.

- Legacy enabled/disabled preference migration, storage persistence while disabled,
  unknown storage names and explicitly selected memory avoiding disk initialization.

- Byte-exact reads across block boundaries, unaligned starts, eviction, restart,
  session cleanup, missing/truncated blocks, capacity limits and free-space loss.
- Loader delivery from disk, short-seek reuse, reads larger than the delivery
  slice, duration-derived prefetch under capacity limits, write/read/creation
  fallback, and rejection of mismatched ranges or representations.
- Existing cancellation, reader-tracking, open-ended refill and playback tests.
- A tvOS device build and an actual run of the user's failing film, with cache
  mode, stalls and recoveries recorded. Successful recovery does not count as
  uninterrupted playback.
