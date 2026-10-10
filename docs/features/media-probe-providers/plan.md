---
status: Draft
created: 2026-08-04
updated: 2026-10-10
summary: Remaining chapter and video codec-tag storage, plus exposing probe provenance through client projections.
---

# Media Probe Providers — plan

> Remaining probe-data gaps for
> [native-client-api](../native-client-api/feature.md) and the web detail page.
> Provider provenance is already stored, and Dolby Vision detail shipped through
> [dolby-vision-profile](../dolby-vision-profile/feature.md). Chapters, video codec
> tags and the client projection of provenance remain open.

## Goal

Persist chapters and video codec tags, and expose them alongside the stored probe
provenance so clients can distinguish missing data from a provider's limitations.

## Current baseline

`MediaSource.ProbeSource` records `Engine` or `Header`. The
`AddProbeSourceProvenance` migration adds the column; ingest, output import and
metadata refresh populate it from `ProbeResult.Source`. Metadata refresh already
uses it to find sources that were read by the header provider. See
[Provenance](feature.md#provenance).

Both providers also populate `DvProfile`, `DvLevel`, `DvBlSignalCompatibilityId`
and `DvElPresent`. These fields are stored per video stream and used by the
library projection and native playback resolver. The flat `HdrFormat` label stays
for existing consumers; it is no longer the only stored dynamic-range detail.

## Target behavior

Written as a diff against [feature.md](feature.md):

- **Chapters.** Extend the probe result, persistence and client projections with
  per-source chapters from providers that can read them. General library chapter
  storage is still absent. Blu-ray inspection and chapter preservation in an
  output MKV belong to [bluray-import](../bluray-import/feature.md) and do not
  supply this library projection.
- **Provenance.** Expose the existing `ProbeSource` through the shared library
  projection to the web detail page and `/native/v1/items/{id}`. Storage and
  refresh behavior already exist; clients do not yet receive this field.
- **Video codec tag.** Persist the source's MP4 sample-entry tag (`hvc1`, `hev1`
  or `dvh1`), read from the header or ffprobe's `codec_tag_string`, so direct-play
  decisions can account for it. Matroska has no MP4 sample-entry tag. Remux writes
  its own sample entry and does not depend on this addition.

## Deliverables

- [ ] D1. **Chapter storage** — entity plus migration, populated by the providers that
      can supply them and left empty by those that cannot.
- [x] D2. **Provenance on the media source** — `MediaSource.ProbeSource`, the
      `AddProbeSourceProvenance` migration and population from `ProbeResult.Source`
      already exist. Client exposure remains in D5.
- [x] D3. **Dolby Vision detail** — the profile, level, base-layer compatibility id and
      enhancement-layer flag (`DvProfile`, `DvLevel`, `DvBlSignalCompatibilityId`,
      `DvElPresent`), stored per video stream, plus a migration and a bounded refresh
      fill-in for rows already labelled `Dolby Vision` without a profile. A flat
      `HdrFormat` stays for existing consumers; this sits beside it. Both providers supply
      it — see [HDR says how sure it is](feature.md#hdr-says-how-sure-it-is). Shipped with
      [dolby-vision-profile](../dolby-vision-profile/feature.md).
- [ ] D4. **Video codec tag** — `hvc1`, `hev1` or `dvh1` as the file carries it (MP4's sample
      entry; ffprobe's `codec_tag_string`; Matroska has none), stored per video stream. It
      is what would let direct play predict the `hev1` Apple rejects; the Dolby Vision
      detail above was split from it because that one had a title playing wrong today.
- [ ] D5. **Client projections** — expose chapters, the stored probe provenance and
      video codec tags through the library projection, web detail page and
      `/native/v1/items/{id}`. Preserve the existing Dolby Vision detail.
- [ ] D6. **Unit tests** — a header-probed source yields no chapters and reports the
      header reader; an engine-probed one yields what the engine returned; a
      profile-8.1 source and a profile-5 source remain distinguishable. Keep the
      existing provider and Dolby Vision coverage while adding the missing chapter
      and projection cases.
- [ ] D7. **`feature.md` update**, index regeneration, and a minor version bump.

## Open questions

- **Is chapter data worth its migration on its own?** It is only visible once a
  client offers chapter navigation, and no client does yet. It may be better
  sequenced with the Apple client's playback surface than shipped ahead of it.
- **Does this block [remux-streaming](../remux-streaming/plan.md)?** No — that
  feature authors the container and so knows what it wrote. But its index walk
  touches the same headers, so building the two together may be cheaper than
  building them apart.

## Verification steps

1. `dotnet test` for the API test project.
2. Probe one file through the external engine and one through the header reader,
   and confirm the stored provenance and chapter presence differ as expected.
