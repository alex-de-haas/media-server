# Artwork Language

Created: 2026-08-15
Updated: 2026-08-15

## Description

Which of an item's cached images each surface shows. Enrich caches every poster,
backdrop and logo the provider offers in the configured languages
(see [metadata](../metadata/feature.md)); this is the rule that picks one, and the
operator's override of that rule for a single title.

The rule exists because a poster has a job: it names the film. TMDb tags each image
with the language of the text printed on it and files art carrying no text at all as
language-neutral, so a language-blind pick shows wordless art about as often as
titled art. Where a surface captions the poster with the title — the web grid and
the tvOS grid both do — that is merely untidy; where it does not, a franchise
becomes a wall of near-identical pictures. The detail hero is the clearest case: the
poster sits beside a logo that *is* the title, and a third-party Jellyfin client
renders whatever the poster wall gives it.

A language tag is evidence, not proof: TMDb's own guidance files a title-less poster
carrying an English tagline under English, so "has Russian text" does not mean "has
the Russian title". The ranking gets the common case right and the pin below settles
the rest.

## Artwork language is resolved per role

There is no artwork-language setting. The head of every chain is the display
language the instance already resolves for titles and overviews
(`SUPPORTED_LANGUAGES[0]`), compared as a whole primary subtag — `ru-RU` matches
`ru`, and `fil-PH` does not match `fi`. What differs per role is the order of the
tiers after it:

| Role | Order, best first |
| --- | --- |
| Poster (`Primary`) | display language → English → any other language → **untagged** |
| Logo | display language → English → **untagged** → any other language |
| Backdrop | **untagged** → display language → English → any other language |

Untagged means the provider reported **no language, an empty one, or the explicit
`xx`** — TMDb emits all three for the same thing. `null` is a language never set,
`xx` is one deliberately set to none (its own UI labels the option
`No Language (xx-XX)`), and an empty string comes through verbatim. Reading `xx` as
a foreign language is what broke backdrop selection in other TMDb clients when the
provider started returning it, so all three collapse into one tier here.

Each order follows from what the image is for:

- **A poster must carry a title**, so textless art is its last resort. A titled
  English poster identifies a film in a Russian library; a beautiful wordless one
  does not.
- **A logo is the title**, rendered in place of the heading on the detail hero, so
  an untagged logo — a language-neutral wordmark like `TENET` — is a real answer
  and outranks a title treatment in a language the reader cannot read.
- **A backdrop sits under locally rendered text**, so text burned into it is a
  defect: the untagged one wins outright, the opposite preference to the poster
  from the same data.

Within a tier the provider's own order stands, then the image tag. `SortOrder` is
just the position an image held in the response it arrived in — TMDb documents no
ordering for those arrays, so it is a weak preference, not a quality signal, and
that is all it is used as. The tag is the key that makes the result *stable*:
because a re-enrich never renumbers rows already stored, two images of one role can
legitimately share a `SortOrder`, and without a total order the winner of such a
tie would be whatever the database happened to return that request.

`ImageSelection` owns all of this, as an expression for the surfaces that rank in
SQL and as a comparison for the surfaces that hold the rows already. Every
surface goes through it: the web detail page and grids, the Jellyfin item mapper
and its image service, catalog (collection-folder) artwork, `/native/v1`,
recommendations, collections, the watched calendar and the removed-titles list.

The Jellyfin mapper and the image service must rank identically, and do, because
that contract addresses artwork two ways: by tag, and by **index** into the
advertised `BackdropImageTags` list. A tag always resolves to exactly the image it
names — ranking decides what is offered, never what a tag means — so a client
holding an older tag keeps getting the image it asked for.

Collection and person artwork have no language axis to rank: a franchise's own
poster and a person's photo are single provider URLs with no image row behind
them. A collection that has no artwork of its own borrows a member movie's poster,
and that borrowed poster does go through the ranking — the earliest member is
chosen first, then the best of its posters.

Seasons and episodes are not enriched, so in practice this is a movie and series
concern.

## The operator can pin a poster

A ranking cannot invent what the provider does not have: for a sequel, TMDb's
localized poster is often the international textless art, and then nothing about
the image says which film it is of. The operator has the last word, and it costs
no provider request because every candidate is already cached.

- `GET /api/library/{id}/images` lists the item's cached candidates — role,
  language, tag and URL — ordered exactly as the surfaces rank them, so the first
  entry of a role is the one on screen, marked `selected`.
- `PUT /api/library/{id}/poster` pins one by tag; `DELETE` hands the choice back
  to the ranking. Both are admin-only. A tag the item does not hold is refused
  with `400`, including the tag of one of its own backdrops or logos — neither is
  a poster and neither can stand in for one.
- The pin is `MediaItem.PreferredPosterTag`, and it outranks every tier on every
  surface. It is stored as the image **tag** rather than a row id or an ordinal:
  the tag is derived from the remote path, so it survives re-enrich and is already
  what the image URLs carry.
- A pin whose image the provider later withdraws is ignored rather than fatal —
  the ranking answers again, and the poster never goes blank.
- In the web UI it is **Choose poster…** on the movie/series detail page, which
  shows the cached posters with the text language of each spelled out.

## Consequences worth knowing

- Changing `SUPPORTED_LANGUAGES` takes effect on the next request. The ranking is
  applied at read time, so nothing is re-fetched or migrated — which is also what
  keeps per-user display language possible later.
- A library that has never requested English artwork has no English tier to fall
  back to until `catalog:refresh-metadata` runs, because enrich only ever adds
  images it has not seen.
- One item may not hold the same remote path twice: `(MediaItemId, RemotePath)` is
  unique, and enrich tolerates a duplicate rather than failing on it. Without that
  a manual refresh racing a catalog-wide one could insert a second row and leave
  the item permanently un-enrichable.
- **The tvOS client keeps its old poster until the app restarts.** It builds the
  poster URL itself and that URL carries no tag
  (`MediaKit/Sources/MediaKit/Library/LibraryStore.swift:31`), while `ArtworkLoader`
  caches bytes in memory keyed by URL and checks that cache before issuing a
  request — so the response ETag never gets to revalidate. A re-ranked or newly
  pinned poster is served correctly by the API but not re-fetched in that session.
  Carrying the poster tag on the sync DTO so the client URL can change with the
  image is [Apple client](../apple-client/feature.md) work, not server work.

## Testing Expectations

Backend tests use xUnit against the in-memory SQLite fixture. Required coverage:

- Each role's tier order, including a titled poster beating textless art, a
  textless backdrop beating a tagged one, a neutral logo beating a foreign one, an
  empty language and the provider's explicit `xx` both ranking as untagged, `fil-PH`
  not matching `fi`, case-insensitive
  language matching, provider order inside a tier, and a shared `SortOrder`
  resolving deterministically.
- The pin: it outranks every tier, survives a re-enrich that adds better-ranked
  art, is ignored when it matches nothing, and is refused for a non-poster tag, a
  blank tag or an unknown item — with the refusals mapped to `400` for a bad tag and
  `404` only for a missing item, so the status names what actually went wrong.
- Both read paths agree — the detail page ranks in memory, the grid ranks in SQL,
  and a pinned poster must appear on both.
- The Jellyfin mapper and image service rank identically: an index-addressed
  backdrop resolves to the tag advertised at that index, an untagged request
  serves the advertised poster, and a tag-addressed request still serves exactly
  that image.
- `/native/v1` offers all three roles by the same ranking, and honours the pin.
- Enrich asks for English artwork even when it is not configured, and does not ask
  for it twice.

A note for whoever writes the next artwork test: the older fixtures seed artwork
with no language, which now lands in the *winning* tier for a backdrop and the
*losing* one for a poster. An assertion about tiers has to neutralize that seed —
give it a language — or it ends up testing the fixture's sort order instead.
