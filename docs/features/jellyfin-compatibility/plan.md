# Jellyfin People Surface — plan

Status: Ready
Created: 2026-08-02
Updated: 2026-08-02

## Goal

Let cast and crew reach Jellyfin clients, so Infuse shows the people on a movie
or episode detail page.

The data is already there: `Person` / `MediaItemPerson` are populated by
`PersonSyncService` and `PersonBackfillService`, and the internal API projects
them (`LibraryReadService.LoadCastAsync` → `CastMemberDto`, plus the person page
under `/api/persons`). The Jellyfin surface simply never projected them —
`BaseItemDto` has no `People` field, `JellyfinItemMapper.MapItem` loads no
credits, and `GET /Persons` is a hard-coded empty result added only so Infuse's
search fan-out would not treat a 404 as "Nothing Found". This is an unbuilt
surface, not a regression.

## Target behavior

Written as a diff against [feature.md](feature.md).

- `BaseItemDto` gains `People`: a list of `BaseItemPerson` (`Id`, `Name`,
  `Role`, `Type`, `PrimaryImageTag`). The field is emitted on the **item detail**
  responses (`GET /Items/{itemId}`, `GET /Users/{userId}/Items/{itemId}`) and
  stays absent from list responses, which must not pay a credit query per row.
- A person has a stable client-facing id in the same 32-character lowercase-hex
  shape as every other id, derived from its provider identity
  (`person|{provider}|{providerId}`) — not from the database row, so it survives
  a rescan exactly like item ids do.
- `GET|HEAD /Items/{personId}/Images/Primary` serves the person's profile photo.
  Clients build image URLs themselves from `PrimaryImageTag`; they do not follow
  the absolute `Person.ProfileUrl` this app stores, so without this route the
  people list renders as name-only placeholders.
- `GET /Persons` stops being a stub and answers with the people who hold at
  least one credit on an item in the library, honoring `SearchTerm` and `Limit`.
- `GET /Items` accepts `PersonIds`, so tapping a person yields their titles in
  this library.

Out of scope, and stated so it is not silently assumed: person biography,
birth/death and place of birth (the person page keeps them), person artwork
other than `Primary`, and any people surface for `Video` items that have no
canonical identity.

## Deliverables

### Phase 1 — people on the detail response

- [ ] **`BaseItemPerson` DTO** in `JellyfinDtos.cs` and `People` on
      `BaseItemDto`.
- [ ] **`JellyfinIds.Person(provider, providerId)`**, mirroring the existing
      `Hex(...)` derivations.
- [ ] **Credit loading on the detail path** in `JellyfinLibraryService`:
      `GetItemAsync` loads `MediaItemPerson` joined to `Person` for the one item
      and passes it to the mapper; `MapManyAsync`'s list callers pass nothing.
- [ ] **Role/type mapping** in `JellyfinItemMapper`: `PersonRole.Cast` →
      `Type = "Actor"` with `Role = Character`; `PersonRole.Crew` → the matching
      Jellyfin person kind for `Director` / `Writer` / `Producer` with
      `Role = Job`. Crew jobs outside that set are dropped rather than emitted as
      an unknown kind (this is what Jellyfin's own TMDb metadata plugin does).
- [ ] **Ordering and cap**: cast by billing `Order`, then crew; a fixed cap on
      the emitted list so a 200-name TMDb credit block does not bloat every
      detail response.
- [ ] **Unit tests** for the id derivation, the role mapping (including the
      dropped-crew case), the ordering/cap, and the list-vs-detail split.

### Phase 2 — person artwork

- [ ] **`PrimaryImageTag`** derived from `Person.ProfilePath` (a hash, like the
      `ImageAsset.Tag` convention), null when the provider has no photo.
- [ ] **Person branch in `JellyfinImageService`**: an id that resolves to
      neither a media item, a catalog, nor a collection resolves to a person and
      serves `Person.ProfileUrl`, fetched on first request and cached to disk
      under a deterministic name (a person has no `ImageAsset` row, so this
      follows the collection-artwork pattern).
- [ ] **`ImageCacheSweeper` awareness** — the sweeper deletes every cache file
      whose name is not in the live set, so person cache names must be
      recomputed into `LiveNamesAsync` alongside `CollectionCacheNames`.
      Without this the sweep silently reclaims every profile photo twice a day.
- [ ] **Unit tests** for the image resolution branch and for the sweeper keeping
      live person files while reclaiming superseded ones.

### Phase 3 — person navigation

- [ ] **Real `GET /Persons`**, replacing the stub: people with at least one
      credit in the library, `Type = "Person"`, `SearchTerm` and `Limit`
      honored. The comment at the stub explaining why it must not 404 moves with
      it, because the search fan-out reason still holds.
- [ ] **`PersonIds` on `JellyfinItemsQuery`** — parsed in
      `JellyfinItemsEndpoints.ParseQuery` and applied in `ResolveItemsAsync`.
- [ ] **`GET /Items/{personId}`** resolves a person to a `Person` item instead
      of 404, so a client that fetches the person before listing its titles does
      not dead-end.
- [ ] **Unit tests** for the query filter, the search, and person-id resolution.

### Closing the plan

- [ ] **Docs** — update [feature.md](feature.md): the endpoint list (`/Persons`
      moves out of the "no data for" group), the `BaseItemDto` field summary,
      the media model mapping, and testing expectations. Delete this plan.
- [ ] **Index** — `node scripts/docs-index.mjs --fix`.
- [ ] **Version bump** — new functionality: `0.46.0` → `0.47.0`. One PR for all
      three phases, per the repository's one-PR-per-feature rule.

## Decisions

- **How many credits to emit.** Cast is capped at 30 by billing order, crew at
  10 after job filtering. Jellyfin returns the whole block, but a TMDb credit
  list runs to hundreds of names and every one of them costs bytes on a response
  Infuse fetches for each title it displays.
- **Which crew jobs.** Directing, Writing and Producing only. TMDb's job strings
  map onto Jellyfin's person kinds (`Screenplay`/`Story`/`Writer` → `Writer`),
  and `Role` carries the original job so the client can still show "Screenplay"
  rather than a flattened "Writer". Everything else is dropped, matching what
  Jellyfin's own TMDb plugin stores.
- **`Fields=People` on list responses.** Not implemented. People stay on the
  detail path so list queries keep their current cost; no observed client asks
  for the field on a list.
- **A non-empty `/Persons` may change search results.** Accepted, and verified
  rather than assumed: the stub exists because Infuse's search fans out to that
  route, so verification step 6 re-checks title search after it starts returning
  people.
- **Backfill coverage is a data question, not a surface one.** People are only
  as complete as the cached TMDb payloads `PersonCredits.Parse` reads. A title
  whose credits were never fetched will show none, and that is
  `PersonBackfillService`'s job, not this plan's.

## Verification steps

1. `dotnet test` for the API test project.
2. `curl` an item detail on the Jellyfin endpoint and confirm `People` is
   present, ordered, and carries `PrimaryImageTag` for people with photos —
   and that a list query for the same catalog does *not* carry `People`.
3. Fetch `/Items/{personId}/Images/Primary` and confirm the photo is served,
   then confirm the cached file survives an `ImageCacheSweeper` pass.
4. Open a movie and an episode in Infuse and confirm the people appear with
   names, roles, and photos.
5. Tap a person in Infuse and confirm their titles in this library are listed.
6. Search in Infuse for a title and confirm the results are unchanged from
   before the `/Persons` stub was replaced.
