# Media Probe Providers — plan

Status: Draft
Created: 2026-08-04
Updated: 2026-08-04

> Two gaps found while building
> [native-client-api](../native-client-api/feature.md), which promised its item DTO
> would carry both and then could not: neither exists anywhere in the schema.
> They are probe concerns, so they live here rather than in a client feature.

## Goal

Record two things a probe already knows but currently discards, so a client can
show them.

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

## Deliverables

- [ ] **Chapter storage** — entity plus migration, populated by the providers that
      can supply them and left empty by those that cannot.
- [ ] **Provenance on the media source** — which provider answered, plus a
      migration.
- [ ] **Surface both** in the library projection, so the web detail page and
      `/native/v1/items/{id}` gain them together.
- [ ] **Unit tests** — a header-probed source yields no chapters and reports the
      header reader; an engine-probed one yields what the engine returned.
- [ ] **`feature.md` update**, index regeneration, and a minor version bump.

## Open questions

- **Is chapter data worth its migration on its own?** It is only visible once a
  client offers chapter navigation, and no client does yet. It may be better
  sequenced with the Apple client's playback surface than shipped ahead of it.

## Verification steps

1. `dotnet test` for the API test project.
2. Probe one file through the external engine and one through the header reader,
   and confirm the stored provenance and chapter presence differ as expected.
