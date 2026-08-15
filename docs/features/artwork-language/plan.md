# Artwork Language — a poster that says which film this is

Status: Ready
Created: 2026-08-15
Updated: 2026-08-15

## Goal

Make every surface show a poster that **names the title**, in the operator's
language where TMDb has one, without moving the rest of the artwork off that
language.

The reported symptom is that a Russian-language library shows posters with no
title text, which makes the parts of a franchise indistinguishable from each
other. The cause is not that the wrong language is preferred — it is that **no
language is preferred at all**.

### Nothing ranks artwork by language

Thirteen call sites select artwork, and twelve of them order by
`ImageAsset.SortOrder` alone — which is nothing more than the position the image
held in TMDb's `/images` response array (`TmdbMetadataProvider.cs:165`):

| Site | Types | Selection today |
| --- | --- | --- |
| `LibraryReadService.cs:520` `ImageUrl` | Primary, Backdrop | `OrderBy(SortOrder).First()` |
| `LibraryReadService.cs:528` `LogoUrl` | Logo | language-aware (the only one) |
| `LibraryReadService.cs:475` `PostersAsync` | Primary | `GroupBy → OrderBy(SortOrder).First()` |
| `JellyfinItemMapper.cs:312` | Primary | `OrderBy(SortOrder).First()` |
| `JellyfinItemMapper.cs:318` | Logo | `OrderBy(SortOrder).First()` |
| `JellyfinItemMapper.cs:329` `BackdropTags` | Backdrop | `OrderBy(SortOrder)`, whole list |
| `JellyfinImageService.cs:165` `ResolveAssetAsync` | any | `OrderBy(SortOrder)`, then tag or index |
| `JellyfinCatalogArtwork.cs:44` | Backdrop | `AddedAt desc, SortOrder` |
| `JellyfinCatalogArtwork.cs:80` | Backdrop | `AddedAt desc, SortOrder` |
| `NativeImageEndpoints.cs:86` `BuildAsync` | Primary, Backdrop, Logo | **no ordering at all** |
| `RecommendationFeedService.cs:236` | Primary | `GroupBy → OrderBy(SortOrder).First()` |
| `WatchHistoryCalendarService.cs:236` | Primary | `GroupBy → OrderBy(SortOrder).First()` |
| `RemovedTitlesService.cs:88` | Primary | `GroupBy → OrderBy(SortOrder).First()` |
| `CollectionReadService.cs:110` | Primary | earliest `Year`, then `SortOrder` |

So the poster on screen is whichever of the Russian, English and untagged
candidates TMDb happened to return first. `ImageAsset.Language` is populated on
every row (`EnrichService.cs:112`) and read by exactly one of these fourteen
paths.

The native surface is worse than arbitrary-but-stable: `BuildAsync` projects
`{ ImageType, Tag }` only — dropping both `SortOrder` and `Language` — and takes
`FirstOrDefault` in whatever order SQLite returns rows. The Apple clients get an
unspecified poster **and an unspecified logo**.

### Untagged means "no text", so textless art wins by default

TMDb's contributor convention is that an image's `iso_639_1` names the language of
the text printed on it, and that art with no text at all is filed under **No
Language** (`null`). The enrich fetch asks for exactly those
(`include_image_language=ru,en,null`, `TmdbMetadataProvider.cs:112`) and stores
them all in one flat pile. A language-blind pick therefore treats "the Russian
one-sheet", "the English one-sheet" and "the textless art" as
interchangeable — and any time the textless entry sorts first, the library shows
a picture with no words on it. That is the reported bug, and it is a coin flip
per title rather than a setting anyone chose.

### The one language-aware pick is inconsistent and subtly wrong

`LibraryReadService.LogoUrl` prefers the configured language, then `en`, then
untagged. Two problems:

- It compares a **two-character prefix** (`preferred[..2]`,
  `LibraryReadService.cs:537`) rather than the whole primary subtag — the exact
  bug `MetadataLanguage.Pick` was written to avoid, which reads `fil-PH` as
  Finnish (`MetadataLanguage.cs:28`).
- No other surface does it. On the Jellyfin and native surfaces the logo is
  picked blind — and the logo **is the visible title** on the detail hero
  (`src/web/src/components/media-detail.tsx:573`, where `logoUrl` replaces the
  `h1`). An arbitrary logo is an arbitrary title.

### Why a single "artwork language" setting is the wrong shape

The obvious fix — an optional setting for the poster's language — cannot be
stated coherently, because the three artwork roles want *three different
answers*, and a setting has only one:

| Role | What it must carry | Correct preference |
| --- | --- | --- |
| Poster (`Primary`) | a readable title, so the grid is scannable | language first, **textless last** |
| Logo (`Logo`) | the title itself, as the hero heading | language first (as today) |
| Backdrop (`Backdrop`) | background for locally-rendered text | **textless first**, language after |

A knob that moved the poster to English would move the hero title to English with
it; a knob that kept the hero Russian would keep the titleless poster. The
preference is a property of what the image is *for*, not of the instance.

## Target behavior

Written as a diff against [metadata/feature.md](../metadata/feature.md), whose
Language Model section is the current statement of how languages are chosen.

### Artwork language is resolved per role, from the display language

There is **no new setting**. The head of every chain is the display language the
instance already resolves for titles and overviews
(`MediaServerSettings.PreferredLanguage`, the first entry of
`SUPPORTED_LANGUAGES`), compared as a whole primary subtag via the existing
`MetadataLanguage` helper. What differs per role is the order of the tiers after
it:

```text
Poster    display → en → any other tagged language → untagged
Logo      display → en → untagged           → any other tagged language
Backdrop  untagged → display → en           → any other tagged language
```

Within a tier the provider's own order stands (`SortOrder`), which is TMDb's
vote ordering — so the tier decides the language and the community decides the
image.

**Untagged means null *or* empty.** TMDb's `iso_639_1` is stored verbatim
(`TmdbMetadataProvider.cs:164`), and an empty string is preserved rather than
normalized, so today's `LogoUrl` — which compares against literal `null`
(`LibraryReadService.cs:547`) — silently misses a neutral image tagged `""` and
falls through to its unordered last resort. The helper treats both as one tier.

The tiers say three things worth stating plainly:

- **Poster: untagged is last, not second.** This is the whole fix. A titled
  English poster is more useful in a Russian library than a beautiful wordless
  one, because the poster's job in a grid is to identify a film. An operator who
  disagrees has the per-item override below.
- **Logo: untagged stays second.** A logo has text by definition; an untagged one
  is a language-neutral wordmark (`TENET`, `WALL·E`) and is a legitimate answer,
  which is why today's `LogoUrl` order is kept rather than reshuffled.
- **Backdrop: untagged is first.** The UI draws its own localized title over the
  backdrop (`media-detail.tsx:553-581`), so burned-in text of any language is a
  defect there — the opposite preference to the poster, from the same data.

### English is always requested

`GetImagesAsync` builds `include_image_language` from `SUPPORTED_LANGUAGES` plus
`null` (`TmdbMetadataProvider.cs:112`). An instance configured `ru-RU` alone
therefore caches no English artwork and the chain above dead-ends at untagged.
`en` is appended unconditionally — one request either way, and image binaries are
fetched lazily (`ImageAsset.LocalPath` is null until a client asks), so the extra
rows cost rows.

Two consequences to be honest about:

- **Existing items gain the English tier only after a refresh.** The chain starts
  working immediately over what is already cached, but a library that has never
  requested `en` has no English rows to fall back to until
  `catalog:refresh-metadata` runs. That is an operator action, not a migration,
  and it is worth saying in the release note rather than hiding.
- **That refresh will collide `SortOrder`.** `UpsertImagesAsync` never updates an
  existing row (`EnrichService.cs:102-105`), so a new poster takes its index from
  the *new* response while the incumbent keeps its old one — two rows can both
  hold `SortOrder = 0`. Since SQLite leaves such a tie unspecified, the current
  `OrderBy(SortOrder).First()` would start flapping between requests. The rank
  therefore has to be a **total** order: language tier, then `SortOrder`, then
  `Tag` as the final deterministic tiebreak. Renumbering rows at write time would
  also fix it, but a total order at read time fixes it for data that is already in
  the table.

### One selection helper, not fourteen selection expressions

A new `ImageSelection` beside `MetadataLanguage` owns the rules above and is the
only place they exist:

- an `Expression<Func<ImageAsset, int>>` rank per role, for the sites that select
  in SQL (a conditional `OrderBy` already translates in this codebase —
  `IdentifyService.cs:317`);
- an in-memory `Pick` for the sites that already hold the rows;
- one `IQueryable` extension for the *"best Primary per item"* shape, which is
  currently copy-pasted verbatim across four services
  (`LibraryReadService`, `RecommendationFeedService`, `WatchHistoryCalendarService`,
  `RemovedTitlesService`).

Deleting that duplication is part of the deliverable, not a side effect: four
identical hand-written picks are how the rule came to be missing from all four.

Six of the sites cannot see a language even if they wanted to, because their SQL
projection drops the column: `LibraryReadService.PostersAsync`,
`JellyfinCatalogArtwork.GetLatestBackdropTagsAsync`,
`NativeImageEndpoints.BuildAsync`, `RecommendationFeedService.LocalArtworkAsync`,
`WatchHistoryCalendarService.PostersAsync` and
`CollectionReadService.PosterFallbackAsync` each select a narrow tuple.
Widening those projections is part of the work.

### The mapper and the byte resolver must rank in lockstep

`JellyfinItemMapper` advertises tags and `JellyfinImageService.ResolveAssetAsync`
serves bytes, and the two order independently today (`JellyfinItemMapper.cs:312`,
`JellyfinImageService.cs:167`). They must apply the *same* rank, because the
Jellyfin contract addresses artwork two ways:

- by tag — safe, since `ResolveAssetAsync` looks a tag up before falling back to
  the index (`JellyfinImageService.cs:172`);
- by **index** — `/Items/{id}/Images/Backdrop/{n}` indexes into the
  `BackdropImageTags` list the mapper published (`JellyfinItemMapper.cs:327`), so
  if only one side re-ranks, `Backdrop/1` serves an image the client was told is
  `Backdrop/0`.

A single shared rank is what keeps that contract intact, and it is the reason the
helper exposes the same rule as both an expression and an in-memory comparison
rather than being applied ad hoc per site.

### Nothing is re-fetched, re-written, or migrated

`ImageAsset.Language` is already populated for every cached row, so the change is
read-side only: no migration, no re-enrich, and an operator who reorders
`SUPPORTED_LANGUAGES` sees the new artwork on the next request.

**Considered and rejected:** baking the rank into `ImageAsset.SortOrder` at
enrich time. It is tempting — every read site already orders by `SortOrder`, so
zero of them would change, and Infuse would inherit the fix for free. It is
rejected because it makes the *policy* a property of stored data: changing the
preferred language would need a full `catalog:refresh-metadata` over every
catalog, and per-user display language — already named as a later additive change
in [metadata/feature.md](../metadata/feature.md) — cannot be served by one baked
ordinal at all. The read-side helper is the same work done where it can still be
parameterized.

### What this does not touch

- **Collection and person artwork** have no language axis to rank: a BoxSet's
  poster is `MovieCollection.PosterUrl` and a person's photo is
  `Person.ProfileUrl`, both single remote URLs with no `ImageAsset` row
  (`JellyfinImageService.cs:182-225`). Only `CollectionReadService`'s *fallback*
  poster — borrowed from a member movie — goes through the chain.
- **Seasons and episodes** are not enriched by the pipeline
  (`ProcessingStages.cs:287`), so they normally hold no `ImageAsset` rows at all;
  the chain is a movie/series concern in practice.
- **The Apple clients need no change.** They consume the URLs the server hands
  them (`src/apple/MediaKit/Sources/MediaKit/Library/TitleDetail.swift:101`), so
  which asset those URLs point at is entirely a server decision — `manifest.json`
  moves, `MARKETING_VERSION` does not.

### The operator can pin a poster

The chain is a heuristic, and for a franchise the residual failure is exactly the
reported one: TMDb's Russian entry for a sequel may itself be textless art, so no
ranking can conjure a title that was never uploaded. Every candidate is already
cached in `ImageAssets`, so the last word costs no provider request:

- `GET /api/library/{id}/images` returns the item's cached candidates (type,
  language, tag, URL).
- `PUT`/`DELETE /api/library/{id}/poster` pins or clears one, admin-gated like the
  other item-level overrides.
- `MediaItem.PreferredPosterTag` (nullable) persists it. The **tag**, not the row
  id or the ordinal: it is `MD5(RemotePath)` (`EnrichService.cs:135`), so it is
  stable across re-enrich, unaffected by any renumbering, and already what the
  Jellyfin and native image URLs carry.
- A pin outranks the chain on every surface — it is the operator's answer to the
  same question — and is ignored (not cleared) if the image ever disappears from
  the provider.
- Web: a **Change poster** control on the detail page, in the pattern of the
  existing item actions.

## Deliverables

Implemented on one branch as one PR, per `AGENTS.md`.

### Phase 1 — the selection rule

- [ ] `ImageSelection` in `src/api/MediaServer.Api/Metadata/`: per-role rank
      expressions, in-memory `Pick`, and the shared *best-per-item* `IQueryable`
      extension. Primary-subtag comparison reused from `MetadataLanguage`, not
      re-derived as a two-character prefix; `null` and `""` are one untagged tier;
      the order is total — tier, then `SortOrder`, then `Tag` — so colliding sort
      orders cannot flap.
- [ ] `en` appended unconditionally to `include_image_language` in
      `TmdbMetadataProvider.GetImagesAsync`.
- [ ] All fourteen selections routed through the helper: `LibraryReadService`
      (`ImageUrl`, `LogoUrl`, `PostersAsync`), `JellyfinItemMapper`
      (`PrimaryImageTags`, `BackdropTags`), `JellyfinImageService.ResolveAssetAsync`,
      `JellyfinCatalogArtwork` (both backdrop paths), `NativeImageEndpoints.BuildAsync`,
      `RecommendationFeedService`, `WatchHistoryCalendarService`,
      `RemovedTitlesService`, `CollectionReadService.PosterFallbackAsync`.
- [ ] The six narrow SQL projections widened to carry `Language` (and, for
      `NativeImageEndpoints.BuildAsync`, `SortOrder` — it projects neither today
      and therefore orders by nothing at all).
- [ ] `JellyfinItemMapper` and `JellyfinImageService.ResolveAssetAsync` share one
      rank, so index-addressed backdrops still resolve to the advertised tag.
- [ ] The four duplicated *best-Primary-per-item* queries collapse into the one
      extension.
- [ ] Write-path hardening, prompted by this change making a library-wide refresh
      the recommended follow-up: `UpsertImagesAsync` builds its dedup index
      duplicate-tolerantly instead of `ToDictionary`, which throws on a duplicate
      `RemotePath` and would then poison every later enrich of that item
      (`EnrichService.cs:98`), and `(MediaItemId, RemotePath)` gains the unique
      index `MetadataRecord` already has (`MediaServerDbContext.cs:349`), with the
      migration de-duplicating first. `POST /api/library/{id}/refresh` has no
      concurrency guard (`LibraryEndpoints.cs:260`), so the race is reachable
      today.

### Phase 2 — the operator's override

Ships in this PR (approved 2026-08-15).

- [ ] `MediaItem.PreferredPosterTag` (`string?`) and its migration.
- [ ] `GET /api/library/{id:guid}/images` — cached candidates per type, with
      language and tag; no provider call.
- [ ] `PUT`/`DELETE /api/library/{id:guid}/poster` — pin/clear, `400` for a tag the
      item does not have, `404` for an unknown item, gated with
      `AppRoles.AdminPolicy` like the other write routes in `LibraryEndpoints`.
- [ ] The pin outranks the chain in `ImageSelection`, on every surface including
      Jellyfin `ImageTags` and `/native/v1`.
- [ ] Lifecycle: the pin is carried by `RemapService`, dropped with the item by
      `LibraryDeleteService`/`CatalogService`, and untouched by re-enrich.
- [ ] Web: `Change poster…` on the movie/series detail page — a dialog over the
      cached candidates, invalidating the item, its catalog grid and the home rails.

### Phase 3 — verification and docs

- [ ] Tests per the Verification section.
- [ ] `docs/features/artwork-language/feature.md`; amendments to
      [metadata](../metadata/feature.md) (the Language Model gains the artwork
      chains and the unconditional `en`),
      [jellyfin-compatibility](../jellyfin-compatibility/feature.md),
      [native-client-api](../native-client-api/feature.md) and
      [frontend-application](../frontend-application/feature.md); `plan.md`
      deleted; index regenerated.
- [ ] `manifest.json` `0.60.1 → 0.61.0` (new functionality).

## Phases

One branch, one PR (per `AGENTS.md`). Phase 1 is shippable on its own and fixes
the reported symptom; phase 2 is what makes a wrong answer correctable rather
than permanent.

## Open questions

None. All five were resolved in chat on 2026-08-15 and are recorded below as
decisions; nothing in this plan is waiting on an answer.

### Resolved on approval, 2026-08-15

- **The per-item poster pin ships in this PR** rather than being cut. It is the
  half that answers the franchise case for certain — where TMDb simply has no
  titled Russian poster, no ranking can invent one.
- **The chain starts from the global `PreferredLanguage`**, matching what titles
  already do. `EnrichService.ResolveLanguages` puts a catalog's `metadataLanguage`
  first when *fetching* (`EnrichService.cs:124`), but every read surface resolves
  display metadata with the global preference (`LibraryReadService.cs:509`) and
  ignores the override — so an Anime catalog pinned to `ja` shows a Russian title
  today. Artwork follows the title rather than diverging from it. Making the
  display language honour the catalog override is a real gap, and it is **its own
  change**: it moves titles and listing order, which this plan is not about.
- **No "prefer textless" setting.** The per-item pin covers the operator who wants
  clean art on a particular title, and a global inversion would be a knob
  contradicting the default the plan just argued for. Revisit only if the default
  proves wrong in practice.
- **`original_language` does not join `include_image_language`.** It would add a
  native-language tier for films whose display language has none, but it widens
  what is cached for every title in the library to serve a narrow case.
- **The within-tier tiebreak stays the provider's order.** `SortOrder` is TMDb's
  own array position, which is its vote ordering, and `AppendImages` makes it a
  dense per-type rank (`TmdbMetadataProvider.cs:155-165`). TMDb's `vote_average`
  and `vote_count` are present in the payload and discarded today; persisting them
  would make the tiebreak explicit at the cost of two columns and a migration, and
  is deferred until the proxy visibly misfires.
- **Within-tier tiebreak.** TMDb's array position is used as a proxy for its vote
  ordering. Persisting `vote_average`/`vote_count` on `ImageAsset` would make the
  tiebreak explicit and survive a provider reordering, at the cost of a column
  and a migration. Deferred unless the proxy visibly misfires.

## Verification

- `ImageSelection` unit tests — each role's tier order, including: poster prefers
  a `ru` over an untagged candidate and an `en` over an untagged one; backdrop
  prefers untagged over `ru`; logo keeps `display → en → untagged`; `fil-PH` does
  not match `fi` (mirroring `MetadataLanguageTests.cs:103`); an `iso_639_1` of
  `""` ranks with `null` rather than as a foreign language; ties inside a tier
  fall back to `SortOrder`; an item with only untagged art still yields one.
- `LibraryReadServiceTests` — the existing logo-language cases keep passing, and
  new poster cases cover the chain, the pin, and an item with no images.
- `JellyfinMappingTests` — `ImageTags["Primary"]`/`["Logo"]` follow the chain and
  the pin; `BackdropImageTags` leads with untagged art; a tag issued by the mapper
  still resolves in `JellyfinImageService` (tag lookup precedes index —
  `JellyfinImageService.cs:167`).
- Native — `NativeImageUrlsTests` gains language cases proving `BuildAsync` is
  deterministic and chain-ordered for all three types. Its fixture never sets
  `SortOrder` today (`NativeImageUrlsTests.cs:46-58`), so the current tests cannot
  detect an ordering change at all; widening the fixture is part of the work.
- `RecommendationFeedServiceTests`, `WatchHistoryCalendarServiceTests`,
  `RemovedTitlesServiceTests`, `CollectionReadServiceTests` — each grid poster
  follows the chain via the shared extension.
- Enrich — `include_image_language` contains `en` even when `SUPPORTED_LANGUAGES`
  does not; re-enrich still neither duplicates nor reorders rows; a duplicate
  `RemotePath` already in the table no longer throws; and two rows sharing a
  `SortOrder` resolve to the same image on every call (the `Tag` tiebreak).
  `FakeMetadataProvider` (`Fakes.cs:29`) returns one `en` poster today and needs
  language variety; `EnrichService` already takes `MediaServerSettings`
  (`EnrichService.cs:17-22`), so no test wiring changes for a new dependency.
- Poster override — pin, clear, unknown tag (`400`), unknown item (`404`),
  non-admin (`403`), and survival across a re-enrich and a remap.
- Web unit tests for the dialog; `src/web/e2e/detail.spec.ts` for pinning a poster
  and `src/web/e2e/catalog-browsing.spec.ts` for the grid tile that follows it.
- `dotnet build`, `dotnet test`, `pnpm lint`, `pnpm test`, `pnpm build`,
  `pnpm exec playwright test`, `bash scripts/validate-manifest.sh`,
  `node scripts/docs-index.mjs --check` — the full CI set (`.github/workflows/ci.yml`).
