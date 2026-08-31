# Single Catalog per Title

Created: 2026-07-26
Updated: 2026-08-31

A movie or series exists in at most one catalog. The constraint is deliberate
and temporary: real multi-catalog membership (one record belonging to several
catalogs) is deferred by
[library item tombstones](../library-item-tombstones/feature.md), but the
*divergence* it causes is a problem today — favoriting the 4K copy of a film
did nothing for the same film in another catalog, and watched state split the
same way. While a work has exactly one `MediaItem`, its user data cannot
diverge.

Different *versions* of one title are unaffected: they were always modelled as
multiple `MediaSource` rows on a single item, so a 4K copy belongs beside the
1080p one rather than in a parallel item.

## The gate

Identification is where duplicates were born, and where they are now stopped.
`IdentifyService` resolves the pinned and the searched path into one identity,
then checks it once: if a **published** item elsewhere already holds that
identity — the movie itself, or the series an episode belongs to — the file is
parked as `NeedsReview` and no item is created. The reason names the catalog:

> 'Inception' (2010) is already in catalog 'Movies' — a title lives in one
> catalog only. Retarget this download to that catalog, or skip it.

Two cases deliberately pass the gate: an identity already present **in this
catalog** (adding another version is the ordinary path), and a **tombstone
elsewhere** (a ghost carries
no files to conflict with and is adopted instead). A tombstone *here* does not
wave a title through: reviving it while another catalog publishes the same
identity would mint the very pair the gate exists to prevent.

The operator's own actions run the same check. `MatchAsync` and
`AssignExtrasAsync` create library items exactly as identification does, so
picking an identity another catalog owns is refused with a `409` naming the
two ways forward — a work lives in one catalog whether a machine or a person
chose the identity.

When one batch collides with **several** catalogs (a franchise pack whose
films live apart), every reason is reported but no retarget destination is
recorded: moving the ingest to one of them would leave the others conflicting.

`IngestItem.ConflictCatalogId` records the catalog that owns the title. The
orchestrator writes it when parking and clears it on every claim, so it always
reflects the last identification pass rather than a stale one.

## Retarget

The review offers **Move download to _catalog_**, which re-homes the whole
ingest into the catalog that owns the title; publishing there merges it as
another version. The destination is the conflict the server recorded — never
a client-supplied catalog — so the operator confirms a decision rather than
making one.

Staged files keep their catalog-relative paths
(`.incoming/<downloadId>/…`, unique per download), so re-homing is one
directory move with no path rewrite: the source files, the ingest, and any
surviving download row simply point at the new catalog, and identification
re-runs from scratch there. Every non-terminal mapping is dropped first —
a mixed batch (one film auto-matched, another conflicting) would otherwise
keep a confirmed file pointing at an item in the catalog it just left, while
the organizer files it under the new root.

Two refusals are honest rather than incidental:

- **Cross-volume** — the organizer hardlinks staging into the library with a
  plain move; copying across volumes belongs in a progress-reporting job (see
  `LibraryMoveService`), not in a review click.
- **Not staged** — a scan-imported ingest's files sit in the catalog's own
  library area, so there is no download to send anywhere.

`IngestItemResponse.CanRetarget` is false in the second case and the review
dialog shows the repair that does work — move the existing title into this
catalog from its library page, then retry — instead of a button that would
always fail. Catalog scans therefore create no duplicate item either: their
files hit the same gate and park with the same reason.

## The audit is gone

A scan used to report every title published in more than one catalog, and the
Settings **Library health** section listed each with its repair. Both were
removed once the gate above had been in place long enough for the library to
hold none: the audit could only ever find pairs that pre-dated the rule, and
every path that could mint a new one now goes through the gate.

Should one appear anyway, the repair is unchanged — move one copy into the
other's catalog, which merges the pair into a single title with two versions.

## No database backstop

The plan called for a partial unique index over published movie/series
identity as a last line of defence. It was built and then rejected on
evidence, and is deliberately absent:

- creating the index fails with SQLite error 19 on any existing database that
  already holds a duplicate pair, so upgrading such a server would leave the
  app unable to start;
- a pair is only repairable **while both rows exist** — the move-with-merge
  flow needs them side by side — so the invariant cannot hold before the
  repair it enables;
- subsystems built to cope with two copies (watch-history's
  `AmbiguousLocalIdentity`, recommendations' multi-copy handling) would become
  unreachable code guarding a state the schema forbids.

The rule is therefore enforced where duplicates are born and tolerated by
everything that already handles them.

## Testing Expectations

- `CrossCatalogGateTests` — the gate parks movies and series instead of
  duplicating; same-catalog version merges are unaffected; an operator match
  cannot pick an identity another catalog owns; a local tombstone does not
  wave through a title published elsewhere; the full retarget cycle re-homes
  staging (clearing mappings the batch already made) and publishes as a
  second version; scan-imported conflicts park, create no item, and report
  `CanRetarget = false`; refusals for unconflicted and unknown items.
- Web e2e (`activity.spec.ts`) — the conflict banner renders the server's
  reason and its button fires the retarget request.
