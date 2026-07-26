# Single Catalog per Title

Status: Draft
Created: 2026-07-26
Updated: 2026-07-26

## Goal

One movie or series exists in at most one catalog. This is a deliberate,
temporary constraint: true multi-catalog membership (one record belonging to
several catalogs) is a deep change deferred by
[library item tombstones](../library-item-tombstones/plan.md) (see its Out of
Scope), but the *divergence* it causes is a real problem today — favoriting
the 4K copy of a movie does nothing for the same movie in another catalog,
watched state splits the same way. While the constraint holds, a work has
exactly one `MediaItem`, so its user data cannot diverge — and the
[Trakt favorites sync](../trakt-favorites-sync/plan.md) duplicate-identity
question dissolves with it.

## Background

Where cross-catalog duplicates can and cannot be born today:

- **Ingest and catalog scan** — the only birthplace.
  `IdentifyService.ResolveMovieAsync` (and the series counterpart) looks up an
  existing item by identity *within the target catalog only*; the same movie
  arriving into a second catalog silently creates a second `MediaItem`.
- **Move between catalogs** — already safe. `LibraryMoveService` resolves a
  merge target by identity in the destination catalog and merges versions
  instead of duplicating; moving a duplicate onto its twin is in fact the
  existing repair path.
- **Remap** — already safe. It resolves its target within the item's own
  catalog.

Different *versions* of one movie are already modeled inside a single item
(multiple `MediaSource` rows), so the constraint costs no functionality — a 4K
copy belongs beside the other version, not in a parallel item.

## Target Behavior

### Ingest gate

When identification resolves a movie or series whose identity already exists
on a **published** item in a *different* catalog, the ingest item goes to
`NeedsReview` instead of publishing, with the reason named plainly ("Dune
(2021) is already in catalog Movies"). Review offers:

- **Retarget** — switch the ingest's target catalog to the existing item's
  catalog; publish then merges it as a new version of that item through the
  existing per-catalog flow.
- **Skip** — the established skip path.

Moving the existing item to the *new* catalog first (existing move feature)
remains the manual alternative for "I want it in 4K catalog now"; the re-run
then merges there.

The gate concerns **published** items only. A same-identity tombstone in
another catalog never routes to review: ingest adopts it silently and re-homes
it to the catalog the user chose (see
[library item tombstones](../library-item-tombstones/plan.md)). There is no
real decision to ask for — the target catalog was named explicitly, and a
ghost carries no files to conflict with.

### Catalog scan

A scan finding a file whose identity is published in another catalog reports
the conflict in the scan result and creates no item. The file cannot be
retargeted (it physically lives under this catalog's root), so resolution is
manual: move the existing item here first, or remove one copy.

### Backstop

A partial unique index on published movie/series identity
(`Kind, IdentityProvider, IdentityProviderId` where `PublicId` is not null,
movies and series only) turns any future gap in the gate into a loud failure
instead of silent divergence. Episodes and seasons need no own index — they
follow their series. Tombstones stay outside the index, so a ghost and a live
copy may coexist until adoption folds them.

### Existing duplicates

The constraint guards the future; the past is audited. A maintenance check
lists works present in more than one catalog, with the move-with-merge flow as
the documented repair; it lives beside the existing missing-files scan report
(`LibraryScanReport`) — one library-health surface, not two. The unique index
ships only after the audit runs clean.

## Deliverables

- [ ] Identity lookup extended with a cross-catalog check at identify time;
      `NeedsReview` reason and the **Retarget** review action (API + web).
- [ ] Catalog scan conflict reporting (no item created, reason surfaced).
- [ ] Maintenance audit listing cross-catalog duplicates, with repair
      guidance, beside the existing library scan report.
- [ ] Partial unique index migration, applied after the audit path exists.
- [ ] Backend xUnit tests (gate at ingest, scan conflict, retarget merge,
      index violation) and frontend tests for the review action.
- [ ] `feature.md`, `plan.md` deleted, index regenerated.

## Phases

One branch, one PR:

1. **Gate.** Cross-catalog check, review reason, retarget action, scan
   conflict.
2. **Audit and index.** Duplicate audit surface, then the unique index.

## Verification

- `dotnet build` and `dotnet test`; frontend test run.
- Manual e2e: ingest a movie into catalog B while it lives in catalog A →
  review with retarget; retarget → second version appears on the catalog-A
  item; scan-based duplicate → conflict reported, no item; audit lists a
  pre-seeded duplicate pair and move-with-merge clears it.
