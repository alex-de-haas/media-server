# Native Playback

Created: 2026-08-04
Updated: 2026-08-29

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

**`resolve` applies all of this**, and puts the answer in the URL it hands back as
`audioStreamId` and `subtitleStreamId`. It also reports which tracks it chose, so a
client's picker ticks what the viewer is hearing rather than what it last asked for.

A request may name tracks itself, and then they win — that is a viewer changing a dub
mid-film. A named track is honoured **only where it belongs to that edition**: one
request resolves every edition of a title, and a track id from one names nothing in
another, so taking it on trust would report a track the source will not carry.

The direct-play path reports neither. The file is served byte for byte, tracks and all,
so the choice was never ours and the player's own picker is the one that works there.

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

## What a client can present

Two vocabularies meet in this decision and nothing else makes them agree. A probe names what it
can see: the header probe cannot tell HDR10 from HDR10+ from a container header, so it reports
the generic **`HDR`**. A client names the formats it decodes — `HDR10`, `Dolby Vision` — and
never says that word.

So an exact match is not enough, for two reasons.

**A stored value can name more than one format.** This library holds `Dolby Vision · HDR10`,
which is what a profile 8.1 file honestly is — and compared whole against a vocabulary of
single names it matches nothing at all. A source is presentable when **any** of the formats it
names is.

**Everything in this vocabulary rests on HDR10**, so a client declaring it can present them all:
Dolby Vision carries a base layer, HDR10+ degrades to its, and a plain `HDR` is a file the probe
could not be more precise about.

A format nothing has heard of is still refused rather than assumed — a refusal a viewer can read
beats a picture nobody can watch.

**The same parsing decides the signalling.** A source stored as `Dolby Vision · HDR10` and
compared whole against `Dolby Vision` matches nothing, and the film would be written with the
cross-compatible entry — a television that can show Dolby Vision quietly getting HDR10 instead,
which is the one thing this feature exists to deliver.

## Which stream is the picture

A file can carry a cover image the muxer never flagged as attached art, and this library holds
such files — a 33.7 GB HEVC remux with an `mjpeg` still beside it. In the database that cover is
a video stream in every way that can be seen: same type, its own index, a codec.

**The picture is the first video stream by index that is not a still image**, and when every
video stream is a still, the first of them: such a file is broken either way, and a reason beats
pretending it has no picture.

The rule is stated in three places and has to mean the same thing in all of them, or two
surfaces disagree about what the film is — the detail projection a client is shown, the resolver
that judges what can be played, and the remux path that writes the sample entry.

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
