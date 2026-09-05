# Apple Client Loading — plan

Status: In Progress
Created: 2026-08-31
Updated: 2026-09-05

> Feed the player its bytes ourselves, instead of handing it a URL and hoping.
> Sits between [apple-client](../apple-client/feature.md) and
> [remux-streaming](../remux-streaming/feature.md), and changes neither's contract.

## Goal

Today the client gives `AVPlayer` a URL and AVFoundation's own HTTP stack decides
everything after that: which ranges to ask for, how far ahead to read, when to stop. We
see none of it and can steer none of it, which is why every lever tried so far has been
blunt — `preferredForwardBufferDuration` did not enlarge the buffer, it sent the player
to re-read the head of the film, and removing it cut a three-minute run from seven
gigabytes to under one.

`AVAssetResourceLoaderDelegate` moves that boundary. An asset opened on a scheme
AVFoundation cannot fetch — `mediaserver://…` — makes it ask *us* for byte ranges
instead. The bytes still come from the same endpoint over the same protocol; what
changes is who decides when to fetch them and how much.

## What the measurements say this is for

From the production range log, on a 70 GB film at roughly 78 Mbit/s:

| Measured over one 13-second window | Rate |
| --- | --- |
| Distinct bytes the film needs | ~70 Mbit/s |
| Bytes actually sent | ~98 Mbit/s (1.39× overlap) |
| Link | ~940 Mbit/s |

**Bandwidth is not the constraint.** The shape of the requests is:

- One long sequential run for the picture — tens of megabytes, efficient.
- **Isolated 64 KB requests for the soundtrack**, spaced one to three megabytes apart:
  roughly seven extra round trips for every second of film, each for a handful of
  audio frames that a window already holding the picture would have contained.
- Overlapping windows that re-fetch the same bytes about 1.4 times.

A read-ahead of our own answers all three from memory. That is the primary goal and the
one this plan can promise.

## Target behaviour, as a diff against today

- **On the remux path only.** `PlayerView` builds its asset the same way whichever decision
  the server returned, so "the player" would mean both; direct play serves a file the server
  never assembled and is out of scope until the end of this plan. The gate is a deliverable
  below rather than an assumption here.
- The player is handed a custom-scheme URL, and a delegate answers its range requests.
- Answers come from a **window held in memory** wherever possible, filled by a single
  forward-reading connection running ahead of the play head.
- Requests to the server become few and large instead of many and small. The isolated
  audio fetches disappear entirely: they fall inside a window that already holds them.
- Everything else is unchanged. Same endpoint, same byte ranges on the wire, same
  container, same decoder — so **Dolby Vision is unaffected**, which is the difference
  between this and the bare-layer experiment that removed it.

## What this does not do

Stated plainly, because the headline symptom is not obviously in scope:

- **It does not make the player ask for more.** AVFoundation still decides which ranges
  it wants. We decide only how fast they are answered. A film that stalls because the
  player stopped asking will still stall — we will merely be able to see it happen.
- It does not reduce what the film costs on the wire.
- It is not HLS, and does not make the buffer knobs work.

Phase 3 addresses the stall directly, and it is the only part of this plan aimed at it.

## Phases

### Phase 1 — a delegate that changes nothing

A custom scheme and a delegate that proxies each requested range to the server one for
one. No window, no prefetch, no cache.

This exists to separate *does the mechanism work* from *does the strategy help*. If the
film plays, seeks and engages Dolby Vision with a pass-through delegate, everything
after it is a change of policy rather than a leap.

- [x] `mediaserver-remux://` asset, delegate on a serial queue.
- [x] Answer `contentInformationRequest`: content type, length, byte ranges supported.
      A wrong answer here is "does not play at all" rather than "plays worse".
- [x] Answer `dataRequest` by range, including `requestsAllDataToEndOfResource`.
- [x] **Only when the decision is remux.** Direct play keeps the plain `AVURLAsset`, so a
      path the server did not assemble is not routed through a loader that assumes it did.
- [ ] Unit tests over the range arithmetic: a request satisfied whole, one satisfied in
      part, one for the end of the file, and one cancelled while outstanding. The first
      three exist; the cancelled one does not, because `AVAssetResourceLoadingRequest`
      cannot be made outside AVFoundation and the loader has no seam for a stand-in yet.
- [x] Honour cancellation: a request AVFoundation drops must stop our fetch with it.
- [ ] Verify on the television: plays, seeks, resumes, **Dolby Vision engages**. This is
      the whole of Phase 1's question — the technique is well-trodden for caching layers
      over `AVPlayer` and unverified here for a progressive MP4 on tvOS — and nothing
      after it is built on an assumption that it works. **2026-09-05: plays, and Dolby
      Vision engages.** Seeking and resuming not yet reported.
- [ ] Verify against the server log: the request pattern is *the same as today*. A
      difference here means the delegate is changing behaviour before any policy has.

### Phase 2 — a window, and one connection filling it

- [x] A byte window over the synthesised stream, filled forward and answering delegate
      requests from memory when it can. Filled by a few **bounded** requests rather than
      one open-ended one: a connection left open while the window is full is one nobody
      reads, and the server aborts a response its reader has stopped taking. Each asks
      for exactly the room there is, and the next starts once a quarter of the budget
      has drained.
- [x] A seek outside the window restarts the fetch rather than crawling to it.
- [x] A bounded budget with an eviction rule — 128 MB ahead, 8 MB kept behind for a reader that lags. The decoder wants its share of an Apple TV,
      and a 4K film at 78 Mbit/s is ten megabytes a second — a minute of read-ahead is
      nearly six hundred **megabytes**. Start at a fraction of that and let the overlay's
      window figures say whether it is enough; spilling to the app's cache directory is
      the answer only if memory turns out too small to matter, and is not planned.
- [ ] Verify: requests per minute of film, and the isolated audio reads, both against
      the same server log this plan was written from. The same log answers whether the
      player's own request pattern changes once answers are instant — it may ask for
      more, or less, or the same, and the plan assumes nothing. **2026-09-05, first run:
      worse, not better** — about twenty requests a second and eight stalls in two
      minutes, because the window anchored on the speculative reader and left the
      play-head reader fetching on its own. The bytes fetched did halve (57 Mbit/s
      against 120 for the same film without the loader): the re-fetching is gone. The
      anchoring is fixed and awaits the next run.
- [x] Direct play is left on the plain `AVURLAsset`. It has the same player and the same
      stalls but no synthesised container, and it earns the loader only once the remux
      path has proved it — as a deliverable of its own, not a widening of this one.

### Phase 3 — a player that got stuck is re-seated

The stall that prompted this: at minute 22 of a 70 GB film the picture froze and
**did not recover on its own**. The server saw eight seconds of silence — no request
arrived — and playback resumed only when the viewer pressed pause and play.

That is the remedy, and it can be automatic. With the delegate we know something no part
of the client knows today: that the player has asked for nothing while its buffer drains.

- [x] Detect it **by absence of demand and of delivery, not by absence of callbacks**. A
      healthy player issues one `requestsAllDataToEndOfFile` and leaves it outstanding while
      we feed it, so seconds without a new callback is what normal playback looks like. The
      signal is that nothing is outstanding *and* nothing has been consumed — the same pair
      the server-side log uses, position and bytes, both still.
- [x] Re-seat the item at the current position, which is what the viewer does by hand.
- [x] Count it where it can be seen, so a fix that fires constantly is not mistaken for
      a fix that works.
- [x] Unit tests over the detector: an outstanding request being fed slowly is **not** a
      wedge, a full buffer consuming nothing is not one either, and both stopped together
      is. These decide whether a working film gets interrupted, so they come before the
      remedy is wired to anything.

### Phase 4 — say what the loader knows

- [x] The overlay gains what only this layer can report: how full the window is, how far
      ahead of the play head it reaches, and how many requests reached the server.
- [ ] Unit tests over the window: eviction under its budget, a seek discarding what is no
      longer ahead, and a refill restarting after the connection was dropped. The first two
      exist; the refill is the loader's, and needs the same stand-in seam as the cancelled
      request above.

## Verification steps

`swift test` first, and the phases above each carry their own cases: the range arithmetic,
the wedge detector, and the window. The detector's are the ones that matter most — they
decide whether a film that is playing perfectly well gets interrupted.

Then the same instruments that found every fault in this area, used the same way:

- **The server's range log** (`PLAYBACK_DIAGNOSTICS`), compared against the run this
  plan quotes: request count, the isolated audio reads, and the overlap factor.
- **The on-screen overlay**: buffer, inflow, stalls, and what the film costs.
- **A long watch on the television**, since the stall this is partly for appeared at
  minute 22 and nothing shorter would have found it.

Each phase states what it expects to see before it is built, so a phase that changes
nothing measurable is a phase that failed rather than one that shipped.

## Related

- [apple-client](../apple-client/feature.md) — the player and its diagnostics.
- [remux-streaming](../remux-streaming/feature.md) — the endpoint being read, and the
  server-side meter this plan's measurements come from.
- [apple-client-core](../apple-client-core/plan.md) — where **Startup on a television**
  still sits, and where HLS remains the alternative to this if it does not deliver.
