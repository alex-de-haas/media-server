# Media Probe Providers — plan

Status: Draft
Created: 2026-08-04
Updated: 2026-08-07

> Gaps found while building
> [native-client-api](../native-client-api/feature.md), which promised its item DTO
> would carry them and then could not: none of them exists anywhere in the schema.
> They are probe concerns, so they live here rather than in a client feature.

## Goal

Record three things a probe already knows but currently discards, so a client can
act on them.

## Target behavior

Written as a diff against [feature.md](feature.md):

- **Chapters.** A probe that reads them (the external engine does; the container
  header reader may not) persists them per media source, and they reach clients
  through the existing projections. Today there is no chapter table, column or
  output at all, so a client cannot offer chapter navigation for anything.
- **Provenance.** Which provider answered a probe is persisted on the media
  source. Today it is not, so a thin stream list is indistinguishable from a
  broken file — the feature document already says a header-read file "reports
  less than one the external engine saw", and nothing downstream can tell which
  happened.
- **Dynamic range in detail.** `HdrFormat` today is one flat value, which cannot
  express what a player needs to decide. See below.

### Dynamic range needs more than one field

The Apple client's resolver is correct today only because every scanned file
happens to be Dolby Vision profile 8.1. Profiles 5 and 7 would break it, and
nothing in the schema records which profile a file carries.

[Swiftfin](https://github.com/jellyfin/Swiftfin), Jellyfin's own Apple client,
models this as eleven values rather than one — `sdr`, `hdr10`, `hdr10Plus`, `hlg`,
`dovi`, `doviWithHDR10`, `doviWithSDR`, `doviWithHLG`, `doviWithEL`,
`doviWithHDR10Plus`, `doviWithELHDR10Plus` — and the granularity earns its keep in
one place in particular:

```swift
if supportsHDR10 || supportsDolbyVision {
    VideoRangeType.doviWithHDR10        // profile 8.1: playable even without DV support
}
if supportsDolbyVision {
    VideoRangeType.dovi                 // profile 5: needs real DV hardware
}
```

Profile 8.1 is announced when the device can do **HDR10 at all**, because its base
layer is backward compatible; profile 5 is announced only with Dolby Vision hardware
decode. One flat `HdrFormat` cannot carry that distinction, so a server holding it
cannot answer "will this play on that device" correctly.

Two further things a probe sees and discards, both needed by
[remux-streaming](../remux-streaming/plan.md) and by direct play:

- **The video codec tag** the file actually carries — `hvc1`, `hev1` or `dvh1`.
  Apple rejects `hev1` outright, so a direct-played `.mp4` tagged that way fails
  with nothing on the server able to predict it.
- **The Dolby Vision profile and `bl_signal_compatibility_id`**, which is what
  distinguishes 8.1 from 8.4 and decides whether a cross-compatible declaration is
  honest.

## Deliverables

- [ ] **Chapter storage** — entity plus migration, populated by the providers that
      can supply them and left empty by those that cannot.
- [ ] **Provenance on the media source** — which provider answered, plus a
      migration.
- [ ] **Dynamic-range detail** — the video codec tag, the Dolby Vision profile and
      its base-layer compatibility id, stored per video stream, plus a migration.
      A flat `HdrFormat` stays for existing consumers; this sits beside it.
- [ ] **Surface all three** in the library projection, so the web detail page and
      `/native/v1/items/{id}` gain them together.
- [ ] **Unit tests** — a header-probed source yields no chapters and reports the
      header reader; an engine-probed one yields what the engine returned; a
      profile-8.1 source and a profile-5 source are told apart.
- [ ] **`feature.md` update**, index regeneration, and a minor version bump.

## Open questions

- **Is chapter data worth its migration on its own?** It is only visible once a
  client offers chapter navigation, and no client does yet. It may be better
  sequenced with the Apple client's playback surface than shipped ahead of it.
- **Can the header reader supply the dynamic-range detail?** `dvcC`/`dvvC` sits in
  the sample entry, which a container-header parser already reaches in an MP4 — but
  in Matroska the same information is in the codec private data and may cost more.
  If only the external engine can answer, the field is null for header-probed
  sources and provenance is what explains why.
- **Does this block [remux-streaming](../remux-streaming/plan.md)?** No — that
  feature authors the container and so knows what it wrote. But its index walk
  touches the same headers, so building the two together may be cheaper than
  building them apart.

## Verification steps

1. `dotnet test` for the API test project.
2. Probe one file through the external engine and one through the header reader,
   and confirm the stored provenance and chapter presence differ as expected.
