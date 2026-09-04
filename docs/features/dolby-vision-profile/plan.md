# Dolby Vision Profile — plan

Status: In Progress
Created: 2026-09-04
Updated: 2026-09-04

> Spans [media-probe-providers](../media-probe-providers/plan.md) (storage),
> [convert-dialog](../convert-dialog/feature.md), [native-playback](../native-playback/feature.md),
> [remux-streaming](../remux-streaming/feature.md),
> [jellyfin-compatibility](../jellyfin-compatibility/feature.md), and the transcode engine's
> [dolby-vision-conversion](https://github.com/alex-de-haas/transcode-engine/blob/main/docs/features/dolby-vision-conversion/plan.md).
> The storage deliverable belongs to the probe plan and is linked from here, not repeated.

## Goal

Tell apart the Dolby Vision a client will play from the Dolby Vision it will quietly show as
HDR10, and give the operator a lossless way to turn the second kind into the first.

## Why

Found on 2026-09-03/04 against the production library:

- The library says `Dolby Vision` whenever ffprobe reports a *DOVI configuration record* on
  the stream. The record's profile, level, layer flags and base-layer compatibility id are
  read by ffprobe and discarded by both the engine and this app.
- Apple TV and Infuse decode single-layer Dolby Vision only — profile 5 and profile 8. A
  profile 7 source (UHD Blu-ray, dual layer) plays as its HDR10 base layer in the native
  client *and* in Infuse, which fetches the file byte for byte over the Jellyfin surface.
- Evidence: *Starship Troopers (1997)* — profile 7, level 6, enhancement layer present,
  compatibility id 6 — plays HDR10. *Avatar (2009)* — profile 8, compatibility id 1 —
  plays Dolby Vision. The library labels both `Dolby Vision`.
- The convert dialog already knows this gap and says so
  ([convert-dialog](../convert-dialog/feature.md#what-the-dolby-vision-warning-does-and-does-not-mean)):
  "the library records only a generic `Dolby Vision`, so the dialog cannot say which of
  these a given source is."
- The remux path has two defects of its own on a profile 7 source: it always writes a
  `dvvC` box even when the source mapping was `dvcC`, and it never reads the per-block
  `BlockAdditions` where profile 7 keeps its enhancement layer and RPU — so the output
  carries a `dvh1` entry with no Dolby Vision metadata behind it.

## Target behavior

Written as a diff against the feature documents this plan spans.

### Storage

Delivered by the **Dynamic-range detail** deliverable of
[media-probe-providers/plan.md](../media-probe-providers/plan.md), ticked there by the same
PR. What this plan relies on:

- Per video stream, nullable: `DvProfile`, `DvLevel`, `DvBlSignalCompatibilityId`,
  `DvElPresent`. `HdrFormat` keeps its vocabulary; the detail sits beside it.
- Both providers fill them from the same 24-byte record: the engine from ffprobe's
  `dv_profile`, `dv_level`, `el_present_flag`, `dv_bl_signal_compatibility_id`; the header
  reader by parsing the record itself — MP4 `dvcC`/`dvvC` in the sample entry, Matroska
  `BlockAdditionMapping` (`BlockAddIDType` dvcC/dvvC, record in `BlockAddIDExtraData`).
  Byte 2 and 3 hold profile (7 bits), level (6 bits) and the rpu/el/bl flags (1 bit
  each); the upper nibble of byte 4 is the compatibility id.
- The header reader thereby reports `Dolby Vision` for Matroska for the first time; today
  it never does.
- The catalog **refresh** pass fills the detail in for rows already labelled `Dolby Vision`
  that have no profile, the same bounded and explicit way it upgrades header-probed rows.

### What the library shows

- The library stream DTO carries `dolbyVision: { profile, level, blCompatibilityId,
  enhancementLayer }` beside `hdrFormat`, null when the stream is not Dolby Vision or the
  detail is not yet known. `/native/v1/items/{id}` carries the same object.
- The web detail shows the dynamic range as **badges** next to the version — `HDR10`,
  `HDR10+`, `HLG`, `Dolby Vision 8.1`, `Dolby Vision 7.6` — built on the existing
  `Badge` component, one badge per format the stream names (a `Dolby Vision · HDR10` value
  yields two). Text marks, not logos: the Dolby Vision mark is a licensed trademark, and a
  styled capsule reads the same. A source with an enhancement layer (profile 7) carries
  the note *Apple TV and Infuse play its HDR10 base layer*.
- Jellyfin `MediaStream` gains `DvProfile`, `DvLevel`, `DvBlSignalCompatibilityId`,
  `ElPresentFlag`, `RpuPresentFlag`, `BlPresentFlag` and `VideoDoViTitle`. `VideoRangeType`
  becomes profile-based for Dolby Vision — `DOVI` for profile 5, `DOVIWithHDR10` for 7 and
  8.1, `DOVIWithHLG` for 8.4, `DOVIWithSDR` for 8.2 — and stays `HDR10` where the profile is
  unknown, which is today's answer. See the open question on what Infuse accepts.
- The tvOS title screen (`TitleView`) shows the same badges, as SwiftUI capsules, from
  `MediaKit`'s `TitleDetail`, which decodes the `dolbyVision` object once the client is
  regenerated from the server's OpenAPI document (`scripts/generate-apple-client.sh`). On
  a profile 7 source the note reads *Plays as HDR10 on this device* — the client knows
  what the device does, the server does not. The Apple client versions on its own
  (`MARKETING_VERSION`), so this bumps it alongside `manifest.json`.

### The remux path honours the profile

- `SignallingFor` asks for `dvh1` only when the source is a profile Apple decodes: 5, or 8
  with compatibility id 1 or 4, *and* the client reported Dolby Vision. The indexer records
  the mapping type and the synthesizer writes the box the record belongs in — `dvcC` for
  profile 7 and below, `dvvC` for 8 and above.
- A profile 7 source (or any source with an enhancement layer) is written as plain `hvc1`
  with **no** Dolby Vision box: its RPU lives in `BlockAdditions` the index never carries,
  so a record would describe metadata the output does not contain. **The viewer still
  sees HDR10, exactly as today** — no Apple device decodes profile 7, and nothing on the
  remux path can change that; what changes is that the server stops claiming `dvh1` for a
  stream it wrote without Dolby Vision. Dolby Vision for such a title comes only from the
  conversion below. The resolution reports `signalling: hvc1`, `sourceDynamicRange` stays
  `Dolby Vision`, and the item's `dolbyVision` object tells the client why.
- A source whose profile is still unknown keeps today's label-based behaviour, so nothing
  regresses before the refresh pass has run.

### Conversion, in the convert dialog

- On a source whose video is profile 7 the video section offers, under *Keep original
  video*, one checkbox: **Convert Dolby Vision to profile 8.1 (single layer)**. Its copy
  says what happens — the Dolby Vision metadata is rewritten to profile 8.1, the
  enhancement layer is dropped, the HEVC picture is copied byte for byte, and Apple TV and
  Infuse then play the result as Dolby Vision — and what is lost: the enhancement layer,
  measured at 1.6 % of such a file in the engine's compression-controls document.
- It applies to a video copy only. A re-encode drops Dolby Vision regardless, and the
  existing warning covers that; the warning becomes profile-aware, using the engine's
  table — profile 5 has no viewable base layer, 8.4 yields HLG, 8.2 yields SDR.
- It is shown only when the engine advertises the tooling (`GET /hardware` →
  `tools.dolbyVisionConversion`), and hidden with the same degradation the rest of the
  dialog already has when the engine is absent.
- `CreateTranscodeRequest` gains `DolbyVision` — `keep` (default) or `toProfile81` —
  forwarded on the wire as `dolbyVision`. `TranscodeService` refuses it with a re-encode and
  on a source that is not profile 7, with a message that says which.
- The imported version's label carries `DV 8.1` when the job converted, next to the
  `Remux`/`Merged` parts it carries today; the output's own probe confirms the profile.
- The result is a new version beside the original, as every conversion today; deleting the
  original remains the operator's step.

### Out of scope, deliberately

- Converting automatically on ingest — a later plan on top of the same engine operation.
- Profile 5 → 8.1, or a conversion that keeps the enhancement layer.
- The chapters and provenance deliverables of the probe plan — untouched.

## Deliverables

Storage ships through [media-probe-providers/plan.md](../media-probe-providers/plan.md)
(*Dynamic-range detail*) and is ticked there. Owned here:

- [ ] **Library and native DTO** carry `dolbyVision`; the OpenAPI document and the
      generated Swift client follow.
- [ ] **Web badges** — dynamic-range badges on the detail page with the profile, and the
      base-layer note for profile 7.
- [ ] **tvOS badges** — `TitleDetail` decodes `dolbyVision`; `TitleView` shows the badges
      and the device note; `MARKETING_VERSION` bumped.
- [ ] **Jellyfin** stream DTO gains the DV fields and the profile-based `VideoRangeType`,
      verified against Infuse.
- [ ] **Remux path**: the indexer records the mapping type; the synthesizer writes `dvcC`
      or `dvvC` to match and writes no Dolby Vision box for a profile 7 source; the
      resolver signals by profile; the resolution reports what was written.
- [ ] **Convert dialog**: profile-aware warning; the conversion checkbox gated on the
      engine's tooling; request field, validation and wire forwarding; version label.
- [ ] **Engine tooling read**: `RemoteTranscodeEngine` reads `GET /hardware`;
      `DisabledTranscodeEngine` reports no tooling.
- [ ] **Unit tests** — xUnit and Imposter on the API: the header reader parsing a
      hand-built record in an MP4 sample entry and in a Matroska mapping (profile 7 with
      enhancement layer, 8.1, 8.4), and reporting null where there is none; the refresh
      fill-in selecting labelled rows without a profile and nothing else; the resolver's
      signalling for profiles 5, 7, 8.1, 8.4 and unknown against DV, HDR10-only and SDR
      clients; the synthesizer's box choice and the boxless profile 7 output; the Jellyfin
      mapper's fields and `VideoRangeType` per profile; `TranscodeService` refusing the
      conversion with a re-encode and on a non-profile-7 source, forwarding it otherwise,
      and the version label. Vitest on the web: the badge labels per `hdrFormat` and profile (one value, a
      two-format value, no profile yet), the warning text per profile and the checkbox
      visible only for profile 7 with tooling advertised. MediaKit tests: `TitleDetail`
      decoding the object and its absence, and the badge and note text per profile.
- [ ] **Documentation**: `feature.md` for this folder; updates to media-probe-providers,
      convert-dialog, native-playback, remux-streaming and jellyfin-compatibility
      `feature.md`; index regeneration; minor version bump `0.69.0 → 0.70.0`.

## Phases

1. **Data** — storage, header reader, engine mapping, refresh fill-in. The header path
   works against an engine that does not yet report the fields; they arrive as null.
2. **Surfaces and signalling** — DTOs, web detail, Jellyfin, the remux path.
3. **Conversion** — dialog, request, validation, label. Needs the engine's
   [dolby-vision-conversion](https://github.com/alex-de-haas/transcode-engine/blob/main/docs/features/dolby-vision-conversion/plan.md)
   shipped; against an older engine the control is hidden and nothing else changes.

One PR in this repository for all three phases; the engine's PR is its own and lands first.

## Open questions

- **Which `VideoRangeType` values does Infuse accept?** The profile-based values are
  Jellyfin 10.9's enum; Infuse decodes the field as required, and an unknown member may
  fail the whole stream. Verified with Infuse's own log before shipping. Fallback: keep
  `HDR10` for every Dolby Vision source and ship only the numeric DV fields.

## Verification steps

1. `dotnet test` for the API project; `pnpm test` for the web; `node scripts/docs-index.mjs
   --check`.
2. After a catalog refresh, *Starship Troopers (1997)* shows a `Dolby Vision 7.6` badge
   with the base-layer note on the web and the device note on the Apple TV; *Avatar (2009)*
   shows `Dolby Vision 8.1` with neither the note nor the conversion control.
3. Convert *Starship Troopers* with the video kept and the checkbox on. On the output,
   `ffprobe` shows `dv_profile=8`, `dv_bl_signal_compatibility_id=1`, `el_present_flag=0`
   and RPU side data on frames; every audio and subtitle track and default flag survives;
   the new version plays as Dolby Vision on an Apple TV 4K in the native client (remux,
   `dvh1`) and in Infuse over Jellyfin.
4. The original profile 7 version, played in the native client, still shows HDR10 — now
   with `signalling: hvc1` and no Dolby Vision box in the output — and a profile 8.1 source
   still arrives as `dvh1`.
5. Infuse lists a Dolby Vision title with the new stream fields and no decode error in its
   log.
