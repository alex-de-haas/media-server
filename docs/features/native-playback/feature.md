# Native Playback

Created: 2026-08-04
Updated: 2026-08-08

## Description

The half of the native surface that makes a title **play**:
[native-client-api](../native-client-api/feature.md) ends where a client can
browse, and this decides what it can open, which tracks it starts on, and records
what was watched.

Three routes under `/native/v1/playback`, all on the same Core-owned identity as
the rest of the surface.

## Resolution

`POST /native/v1/playback/resolve` takes an item and the client's **capability
profile** — containers, video codecs, audio codecs, dynamic ranges, and an
optional channel ceiling — and answers per edition with one of:

- `directPlay` — the original file, with a signed byte-range URL;
- `remux` — the same streams repackaged into a container the client can open;
- `unsupported` — with a machine-readable reason, so a client can say "this copy's
  only audio track is DTS" rather than failing silently.

Reasons are strings rather than an enum on the wire, so an unknown one cannot break
an older client: `unsupported_video_codec`, `unsupported_audio_codec`,
`unsupported_dynamic_range`, `no_audio_track`, `packaging_unavailable`,
`packaging_pending`, `packaging_unsupported_audio`, `no_file`.

This replaces the Jellyfin surface's `EnableDirectPlay`/`EnableDirectStream` flags,
which that surface parses and then ignores because it has only one answer to give.

The profile is a **request body, not a stored entity**, so a fifth axis is additive
and needs no migration. A coarse "device class" was rejected: it ages badly and
cannot express the one distinction the [Apple TV
spike](../apple-client/plan.md#device-pass-on-an-apple-tv-4k-2026-08-03) proved is
real.

### Which signalling a client is served, and where that choice exists

That distinction is Dolby Vision, and it is not cosmetic: a `dvh1` sample entry
engages DV on a device that supports it and **breaks one that does not**, while the
cross-compatible `hvc1` + `dvvC` form reads as HDR10 everywhere.

The choice exists **only on the remux path**, because that is where the container is
written. A `directPlay` answer serves the file byte for byte, so its sample entry is
whatever was put on disk — the response therefore reports `signalling: null` there
and carries `sourceDynamicRange` instead, so a client knows what it is opening
without being promised something nothing keeps.

On remux, a DV source is written as `dvh1` for a client that reported DV support and
as the cross-compatible form otherwise — correct, because profile 8.1's base layer
is HDR10 by definition.

Whether a source can be **presented at all** is a separate question and applies
everywhere: a client with no HDR is refused an HDR source with
`unsupported_dynamic_range`, while a client with HDR10 but no DV is offered a DV
source, since its base layer is HDR10.

One gap follows from this and is not closed here: nothing records a file's stored
sample entry, so a DV file served by direct play goes out as written, and a client
without DV may fail to open one tagged `dvh1`. Recording it belongs with the other
[probe gaps](../media-probe-providers/plan.md).

### Packaging

A source whose codecs are fine and whose container is not answers `remux` with a
URL, served by [`remux-streaming`](../remux-streaming/feature.md). The contract did
not change to accommodate it, which is what it was shaped for.

Three refusals sit under it, and the differences are deliberate: `packaging_pending`
means the background walk has not reached this file and retrying later works;
`packaging_unavailable` means nothing here can index that container and retrying
never will; `packaging_unsupported_audio` means the client could decode the audio but
no sample entry can be written for it, so a remux would play silently.

A `transport` accompanies the URL — `byteRange` today — because delivering the same
repackaging over HLS would be another transport rather than another decision.

## Track preferences

A per-user preference, scoped to the user's default or to one title — for a show
the scope is the series, so a choice made on one episode carries to the next. One
row per scope, enforced by a unique index, so "which one wins" is never a question.

Preferences store **intent, never stream indexes**. An index means nothing across
two editions of the same film: a remux and a smaller cut have different track
layouts, and "Russian dub, no subtitles" is what survives both. Resolution happens
against whatever the chosen source actually holds, and a **sidecar dub is a
candidate like any embedded track** — on this surface it is fetchable, which is the
thing no existing client can do.

Two rules are easy to get wrong and are pinned by tests:

- **No subtitle language asked for means no subtitles.** Silence is a real answer;
  picking one anyway is how a viewer ends up with subtitles they never wanted.
- **Forced-only with no forced track picks nothing**, not the full dialogue track.

`PreferOriginalAudio` beats the language preference when the source has the
original, and falls back when it does not — the flag for a viewer who normally
takes a dub but watches one show subtitled.

Preferences ride the [change log](../native-client-api/feature.md#the-change-log)
as their own entity type, and a sync page carries the scopes that changed —
an item id, or the literal `global` — so a choice made on the Apple TV reaches the
iPhone through the same feed as everything else. Ids rather than the payload: it is
small and rarely changes, so the client re-reads it rather than having its shape
duplicated in two places.

A preference's scope is validated when it is written: a title that does not exist,
or one that is unpublished or tombstoned, is refused with 404 rather than left to
the foreign key, which would produce either a 500 or a preference stored against
nothing.

## Sessions

`POST /native/v1/playback/sessions/start` opens a session and returns its id;
`…/progress` and `…/stop` report against it.

They write through **`UserDataService`** and nothing else — the same path the
Jellyfin surface uses. A second writer is how the watched threshold, the resume
rules, the season and series aggregates, `PlaybackHistoryEntries` and the Trakt
outbox would start disagreeing depending on which client played the file.

The play-session id is **minted by the server**. It is what keeps one viewing from
counting twice when a viewer rewinds past the watched threshold and watches forward
again, so it does not depend on a client being unique.

A start carries the media source and the chosen tracks, because a viewing is of one
edition with one dub rather than of a title, even though reporting needs only the
item and the position today.

Items are addressed by their internal id and resolved to the public one the
reporting path is keyed by; unpublished and tombstoned items are refused, as
everywhere else on this surface.

## Testing Expectations

Backend tests use xUnit and Imposter. Required coverage:

- Resolution across capability profiles: a direct-playable MP4, a DTS-only source,
  a source with one playable track among several, an undecodable picture, a channel
  ceiling, and an MKV both with and without packaging available.
- The Dolby Vision decision in both directions — a client that reports DV support
  and one that does not — and a client with no HDR at all being refused.
- That direct play promises no signalling, and that a ragged capability profile —
  the null and blank entries a client can actually send — is answered rather than
  thrown at.
- That one user cannot end up with two defaults, which the composite index alone
  does not prevent because SQL treats NULLs as distinct.
- Track selection resolved against **two editions with different track orders**
  using the same preference, a sidecar dub as a candidate, the original-audio
  preference winning and falling back, and both silence rules above.
- Preference scoping: a title's override beating the default, clearing falling
  back, a second write updating rather than duplicating, and the change-log rows
  for both upsert and delete.
- Sessions producing user data and history rows indistinguishable from the ones the
  Jellyfin path writes for the same viewing, including that the history row exists
  at all — it is written by the recorder the composition root injects, not by
  `UserDataService` itself.
