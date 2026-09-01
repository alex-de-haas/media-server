# MCP Tools — plan

Status: In Progress
Created: 2026-09-01
Updated: 2026-09-01

M6 in [root](../../root.md): the media server's use cases exposed as MCP tools, so an
agent running on the host can answer *"do I have this?"*, *"why has this not shown up?"*,
*"get me this"*, and repair a bad identification — without the operator opening the web UI.

Hosty already carries the plumbing. An app declares `interfaces.mcp` in its manifest with a
path, Core authenticates the caller and the app authorizes it, and the `hosty mcp` connector
re-exports the tools to whatever agent the operator is using. Nothing here needs new platform
work; this plan is about which tools exist and what they are allowed to claim.

## The scenarios this is measured against

Dictated by the operator, and checked against the code rather than against the tool list —
which is how four of the gaps below were found. A tool set that cannot carry these is not
short, it is wrong.

1. **"Here is a magnet, download it into this catalog"** — and when the catalog is not named,
   ask which one. Covered, and the API already insists: `AddTorrentRequest.CatalogId` is
   required, so the question is not a nicety the agent might skip.
2. **"Has Oppenheimer finished downloading, and if not, how long?"** — asked by *title*, not by
   release name. Partly covered; see the download-to-title gap.
3. **"What went wrong with it?"** — and, unprompted, *"that film never got identified, it needs
   you"* when the operator asks about a title that silently stalled. Covered by the ingest
   tools, plus the same join as (2) and the skill in Phase 3.
4. **"Something about a plane hijacking"**, or "an action comedy". Not covered today; the data
   is closer than it looks. See thematic search.
5. **"What did I rate this?"**, **"recommend something like this film"**, **"track this"**, and
   **"when does it come out / when is the next episode?"** — including for a title that is not
   tracked yet. Ratings and tracking are covered; the other two are not.

Extending the API is expected rather than avoided: some of what follows will be reachable only
through MCP at first, and the web UI can catch up to it later.

## This is not a wrapper over the HTTP API

The API has roughly eighty routes. A tool per route would be a worse interface than none: the
model would spend its context discovering that six of them are ways to say "I watched this".

Tools are shaped by the question the operator asked, not by the endpoint that answers it —
`list_shelf(kind)` covers `/recent`, `/resume` and `/nextup`; `set_title_state` covers six
routes across played, favorite and rating; `control_download` covers pause, resume and
stop-seeding. The target is about sixteen tools.

Two routes cannot be wrapped at all, and are dealt with under Deliverables: neither the
library listing nor the ingest listing can be filtered or paged.

## What the agent must never be able to conclude wrongly

Each of these is a specific false statement a plausible implementation would let an agent
make about this host. They are the reason the tool list is short and the response shapes are
opinionated.

- **"You don't have that film."** Wrong when the catalog was never scanned, when a scan is in
  progress, when the mount is offline, or when the title exists as a tombstone. An empty
  result must say which kind of nothing it is, or the agent will report absence as fact. Two
  of those four have nothing to read today — no scan state is persisted — which is why the
  deliverables below add it rather than assuming it.
- **"There are no failures in the pipeline."** Wrong when it means "none in the first fifty
  rows I was given". Every list result carries the window that produced it — `limit`,
  `returned`, `truncated` — and a truncated result means *there was more*, not *that is all*.
- **"I added it."** Wrong for anything detached. A scan, a metadata refresh and a torrent add
  are all accepted rather than completed; reporting an enqueue as a success is a lie the
  operator only discovers later.
- **"You have watched 40% of your library."** Watch state, ratings, favorites and the
  watchlist resolve through `ClaimsPrincipal` to an app user. A tool that reaches them
  without a user must refuse, not answer for nobody.

Every tool declares `readOnlyHint` explicitly. Hosty's connector filter is fail-closed: a
tool with no annotation is treated as possibly mutating and is not exported at all, so an
unannotated surface is not a permissive surface — it is an invisible one.

## The tools

### Reading the library

| Tool | Backed by |
| --- | --- |
| `search_library` | a **new** paged, searchable listing (see Deliverables) |
| `get_title(id)` | `GET /api/library/{id}` and `GET /api/library/{id}/episodes` — seasons, files, sizes, user state |
| `list_shelf(kind: recent\|resume\|nextup)` | the three shelf routes |
| `list_recommendations(kind?, limit?)` | `GET /api/recommendations` — already takes both arguments |

*"Suggest something to watch"* is the question that shapes this group, and the first draft of
this plan had no tool for it. It must not be answered by pulling titles and reasoning over
them: the recommendation engine already exists, already knows the operator's popularity bias
and what they hid, and already pages. An agent that ignores it and ranks a few hundred rows
from `search_library` will be slower, worse, and inconsistent with what the web UI shows.

What the agent adds on top is filtering by the constraint in the question — an unwatched
comedy under two hours — which is why `search_library` results carry genres, runtime, rating
and watched state, and why that is *all* they carry.

### Why something is missing

The pipeline is where an agent earns its place: `NeedsReview` and `Failed` items are exactly
what an operator does not notice until they go looking for a film that never appeared.

| Tool | Backed by |
| --- | --- |
| `list_ingest(status?, stage?)` | a **new** filtered, paged listing (see Deliverables) |
| `get_ingest_item(id)` | `GET /api/ingest/{id}` — stage history and where it stopped |
| `get_server_status` | catalogs, active scans, `GET /api/vpn`, `/api/dht`, counts per ingest status |

### Acquiring

| Tool | Backed by |
| --- | --- |
| `search_metadata` | `POST /api/metadata/search` — resolves a title to a `providerRef` |
| `list_downloads` | `GET /api/torrents` |
| `add_torrent(catalogId, magnet\|torrentFile, keepSeeding?)` | `POST /api/torrents/add` — not idempotent, answers "accepted". `catalogId` is required by the API, so an agent with no catalog named has to call `list_catalogs` and ask |
| `control_download(id, action: pause\|resume\|stop_seeding)` | the three control routes |

### Repairing an identification

| Tool | Backed by |
| --- | --- |
| `search_ingest_candidates(id, title?)` | `POST /api/ingest/{id}/search` |
| `match_ingest_item(id, groups)` | `POST /api/ingest/{id}/match` |
| `advance_ingest_item(id, action: retry\|skip\|pin\|retarget)` | the remaining stage commands |

A single provider reference is not enough, and a tool shaped that way would be unable to
repair a supported class of `NeedsReview` item. `MatchRequest` carries `Groups`, each with its
own identity and its own set of source files — that is how a franchise pack resolves into
several movies — and each file carries an optional season and episode, which is how an episode
match is expressed at all. The tool therefore takes the same shape the API does: one or more
groups, each naming an identity and the `SourceFileId`s that belong to it.

`search_ingest_candidates` is the other half of the same conversation, and the first draft of
this plan missed it in favour of the generic `search_metadata`. It re-searches *this item* by
its parsed title and returns candidates, which is what the operator is answering when they say
"that one is the 1998 version" — and it beats a generic search because the item's own parse is
the starting point.

This makes `get_ingest_item` a precondition rather than a convenience: the source file ids the
match refers to come from there, so the agent has to look at the item before it can repair it.
The tool description should say so, because a model that guesses ids will produce a
`FileNotFound` outcome that reads like a broken tool.

### Catalogs and space

| Tool | Backed by |
| --- | --- |
| `list_catalogs` | `/`, `/mounts` and `/usage` together, so "how much room is left" is one call |
| `refresh_metadata(catalogId?)` | `POST /api/catalogs/refresh-metadata` — already queued: 202 with the started set, 409 when one is running, and `/refresh-metadata/active` to observe it |
| `scan_catalog(catalogId?)` | `POST /api/catalogs/{id}/scan` — **synchronous today**, see below |

The two are not symmetric, and the first draft of this plan claimed they were. Metadata
refresh is already an observable background job. A scan is not: `CatalogEndpoints` awaits
`CatalogScanService.ScanAsync` and returns 200 with a report once it has finished, with no
guard against a second one starting and no persisted state to ask about.

Over MCP that is worse than untidy. A tool call that blocks for minutes while a disk is walked
will hit whatever timeout the agent's client applies, and the operator gets a failure for work
that is in fact running. Giving scan the same coordinator treatment as refresh is Phase 1 work
below, not something the tool layer can paper over.

### Personal state

| Tool | Backed by |
| --- | --- |
| `set_title_state(id, watched?, favorite?, rating?)` | six routes behind one call |
| `manage_watchlist(action: list\|add\|remove)` | `/api/watchlist` |
| `get_release_calendar` | `/api/watchlist/calendar` |

## Deliberately absent

**Every delete.** Library items, seasons, episodes, tombstones, and torrents-with-data are
all irreversible, and this app has no undo. An agent that mistakes one identifier for another
erases the wrong series, and the operator finds out when they go to watch it. If deletion is
ever added it belongs in its own tool with `destructiveHint: true`, and its description must
require a `get_title` first so the model states what it is about to remove.

**The Jellyfin, native and image surfaces.** Those are protocol surfaces for clients, not use
cases. Streaming does not fit a tool call.

**Transcoding**, beyond a read-only `list_transcodes` if it proves useful. Batch re-encode is
operator-initiated by design, and that design should not be quietly relaxed by an agent.

## Deliverables

Grouped so that each phase leaves the repository in a shippable state.

### Phase 1 — the API the tools need

The listings the MCP surface depends on today return everything they have. `GET /api/library`
filters only by `catalogId` and `kind`; `GET /api/ingest` takes no parameters at all. For an
agent both outcomes are bad in the same way — either the whole library lands in the model's
context, or it is cut silently and "not found" stops meaning anything.

- [x] **A paged, searchable library listing.** Title substring search, plus the existing
      `catalogId`/`kind` filters and a watched-state filter, with `limit`/`offset` and a total
      count in the response. Additive: the existing call with no new parameters keeps
      returning what it returns today, so the web client is not broken by this change.

      **Search and window live on their own query path**, not as parameters bolted onto the existing
      one. The two order differently and cannot be reconciled: the list sorts *after* projection, by
      the localized title a card renders, while a window has to be applied in SQL — and paging one
      order while sorting another lets a row appear on two pages and on none. The search path orders
      in SQL by the preferred language's title, which is close to the rendered order and, more to the
      point, stable.

      Watched state is evaluated the way the library defines it rather than the way the column reads:
      a movie owns its played flag, a series does not — its state is a rollup over published episodes,
      and a series with nothing published is not "finished" by vacuous truth.
- [x] **Enough on a list row to filter a suggestion.** `LibraryItemDto` carries title, year,
      kind, poster and user data — nothing to answer "an unwatched comedy under two hours"
      without fetching every title one by one. Genres, runtime and community rating belong on
      the row. Read from the metadata record the projection already loads, so they cost no extra
      query. The poster URL stays on the HTTP shape for the UI and is dropped in the MCP projection,
      where a model cannot see it and it is pure weight.
- [x] **A filtered, paged ingest listing.** `status`, `stage` and title filters, with `limit`/`offset`
      and a total counted before the window. The title filter searches all three names an item can
      have — the identified title, the pinned target, and the release name — which is what collapses
      the separate "download answerable by title" deliverable below into this one.
- [x] **Case folding outside ASCII.** Measured, not assumed: a Cyrillic title matches `Оппенгеймер`
      and misses `оппенгеймер`, because SQLite's `LIKE` folds ASCII only. Routing the comparison
      through SQL `lower()` makes it strictly worse — that function is ASCII-only too, so lowering the
      term in .NET leaves it matching nothing. The fix is a normalized, case-folded column, which the
      library search needs regardless; the ingest filter should read the same one. Until then that
      filter is exact-case for non-Latin titles, which for a Russian-language library is close to
      useless — this is not a polish item.

      **Done differently than written.** A normalized column would have meant a migration, a backfill,
      and a write path to keep in step, beside every searchable column. SQLite lets an application
      replace `like()` outright, so the fold happens in .NET for every LIKE in the app at once — which
      also fixed the two Jellyfin searches, which had the same bug and no test for it. The cost is
      SQLite's LIKE-to-range index optimization, which only ever applied to prefix patterns; every
      pattern here is `%term%` and scans regardless.
- [x] **A scan that can be started without being waited for.** `CatalogScanService.ScanAsync`
      is awaited by the endpoint, so a scan of a large catalog holds the request open for as
      long as it takes and nothing prevents a second one starting alongside it.
      `CatalogRefreshCoordinator` already solves exactly this for metadata refresh — queued,
      202 with what it started, 409 when one is running, and `/refresh-metadata/active` to
      observe — and a scan coordinator should mirror it rather than invent a second shape.
      Shipped as `POST /api/catalogs/{id}/scan/queue` **beside** the synchronous route rather than
      replacing it: the Catalogs page renders the report that route returns, and the operator can move
      to the queued form whenever the UI does. `/scan/queue` for the library, `/scan/active` to watch.
- [x] **A download answerable by title.** Smaller than it was written: `IngestItemResponse`
      already carried `DownloadId`, `DownloadName`, `MediaTitle` and `MediaItemId`, so the join this
      called for existed and only the *query* was missing. The title filter above supplies it, and
      progress and ETA stay on `DownloadResponse`, reached by the `DownloadId` the row already
      returns. No new join was built.
- [x] **Thematic and genre search over the library.** "Something about a plane hijacking" and
      "an action comedy" are both unanswerable today, and no tool shape fixes that.
      `MetadataRecord` already persists `Overview` and `Genres` as columns, so both are a query
      away. TMDb keywords are parsed already (`TmdbPayload.Keywords`, capped at 16) but live
      only inside the `Raw` JSON — promoting them to something queryable is what turns "plane
      hijacking" from a guess against prose into a match against a tag. Same window contract as
      the rest.

      **Shipped as a `MetadataTag` table**, rebuilt whenever a record is written, because neither
      source can be filtered on where it lives — genres are a converted JSON list and keywords are
      inside the raw payload. `about` searches keywords *and* the synopsis, since the two fail in
      opposite directions: a keyword is precise and sparse, a synopsis complete and vague. Several
      genres mean all of them, not any.

      A backfill worker projects records written before the table existed; without it a settled
      library becomes searchable only as titles happen to be re-enriched, which is never. It walks by
      id cursor rather than by re-querying "records with no tags" — the latter never terminates for a
      record that yields no tags, which is how the first version behaved.
- [x] **A recommendation seeded by a named title.** `RecommendationKind` is only `Movie` or
      `Series`; the engine seeds from what the operator *watched*, and its own note says TMDb
      only answers "what is like X", so the choice of X is the entire personalization. "Suggest
      something like this film" is that machinery with the seed supplied instead of inferred —
      a parameter, not a new engine.

      A title that cannot seed — unidentified, or an episode, since TMDb answers "what is like this
      show" and never "like this episode" — is **refused**. Falling back to the ordinary feed would
      answer a different question than the one asked, with nothing in the response to say so.
- [x] **A release date for a title that is not tracked.** `/api/watchlist/calendar` answers for
      tracked titles only, but "when does it come out" is most often asked about something the
      operator has *not* added yet — and answering it is what prompts them to add it.

      `GET /api/watchlist/calendar/preview` asks the schedule provider directly and persists nothing:
      tracking is what creates a row, and a question should not. A title the provider will not answer
      about is refused rather than reported as undated — "no dates" is a claim about the film, and
      this one would be a claim about the request.
- [x] **Persisted scan state per catalog** — at least "never scanned", "scanning", and when it
      last finished. This is what lets an empty search result say which kind of nothing it is;
      without it that contract is a sentence in a document rather than a behaviour.

      **No new column.** Now that a scan is a job, the job rows already record what happened and when,
      and they are never pruned — `/api/catalogs/scan/state` reads them. A column would be a second
      source to keep in step, which is how "last scanned" ends up disagreeing with the scan that ran.
      Only a *completed* job counts: a catalog whose disk was unreadable must not report a
      last-scanned time, or the empty result it produces reads as "the library really is empty".
- [x] **Regenerate the OpenAPI document and the Apple client** so the new fields reach
      `src/api/openapi` and the generated Swift client rather than drifting from it. CI compares a
      recorded hash of the document against the generated sources, so this is enforced rather than
      remembered. The three new fields were checked in the generated Swift by name: the generator
      skips what it cannot represent and carries on, and a nullable reference is exactly the shape
      that has silently vanished before.

### Phase 2 — the MCP surface

- [x] **`interfaces.mcp` in `manifest.json`** and a JSON-RPC endpoint answering `initialize`,
      `tools/list` and `tools/call`. Authenticated by the same scheme as the rest of `/api`: Core
      answers "who is this" and this app answers "what may they do", so an MCP surface with an
      identity system of its own would be a second answer to the first question.
- [x] **The read tools**: `search_library`, `get_title`, `list_ingest`, `get_ingest_item`,
      `list_shelf`, `list_recommendations`, `search_ingest_candidates`, `search_metadata`,
      `list_downloads`, `list_catalogs`, `get_release_calendar`, `preview_release`,
      `get_server_status`. Thirteen rather than the twelve planned: `preview_release` split from the
      calendar, because the two look interchangeable and are not — the calendar reads the watchlist
      and answers nothing for a title nobody tracks, which is the case the question is usually about.
      Each description says so, and a test asserts it, since a model choosing between them by name
      alone will choose wrong.
- [x] **The write tools**: `add_torrent`, `control_download`, `match_ingest_item`,
      `advance_ingest_item`, `scan_catalog`, `refresh_metadata`, `set_title_state`,
      `manage_watchlist`. Annotated through their own helper so read-only is never a default a write
      tool inherits by forgetting to override it — the annotations are what a client shows the
      operator before letting a call through, so a wrong one is a wrong consent prompt.

      **Two ingest actions the plan listed are not here.** `pin` and `skip` were to ride inside
      `advance_ingest_item`'s action list, and they cannot: pin takes a whole provider identity and
      skip takes a set of file ids, so folding them into an enum gives one tool three argument shapes
      that share nothing. Skip also discards files from an ingest, which puts it with the deletes this
      surface excludes. `advance_ingest_item` carries retry and retarget, which need no arguments of
      their own; a pinning tool can be added on its own terms if the need appears.
- [x] **The window contract on every list result** — `limit`, `offset`, `returned`, `total` and
      `truncated`, reported from what actually ran. `truncated` is *exact* here rather than the
      "a full page may mean more" other surfaces report: these queries count before they cut, so the
      number left behind is known. Inferring it from a full page marks a complete last page as
      truncated, which a test catches.
- [x] **The empty-result contract** — an empty `search_library` says whether catalogs are unscanned
      or being scanned, reading the Phase 1 scan state. Asserted in both directions: a note on every
      empty answer would train the model to skip it.
- [x] **Detached operations answer "accepted"**, with the note saying what was started. A scan
      already under way is reported as such rather than counted as a second start, and a scan of a
      catalog that does not exist says so rather than accepting work that was never going to happen.
- [x] **User resolution** — the acting Hosty user is resolved per call and carried into the tools
      that need it. Filtering by watched state without one is refused: answering for "nobody" reports
      every title as unwatched, which reads as a fact about the library rather than about the caller.

### Phase 3 — the skill

- [ ] **An app-provided skill** (`agent.skillFile` in the manifest). Hosty lets an app hand
      the agent a skill document, and this app needs one more than most: without the pipeline
      vocabulary — `Intake → Identify → Organize → Probe → Publish`, and what `NeedsReview`
      means — a model cannot tell from `list_ingest` whether it is looking at a problem or at
      normal progress.

## Decisions

These were open when the plan was written; the answers are the operator's, recorded here with
what they imply.

- **`add_torrent` gets no approval gate of its own.** It is declared non-read-only, which
  surfaces it as an approval in whichever agent client the operator is using, and that is
  where the decision belongs. A second gate inside this app would ask the same question twice
  and teach the operator to click through both.
- **`get_server_status` reports whether the VPN is up.** It is the first thing to check when
  nothing is downloading. It reports up or down and nothing else — never the endpoint,
  the provider, or any credential.
- **`get_title` answers with a summary, not a record.** Identity, year, kind, genres, runtime,
  official and community rating, watched state, season and episode counts, and sources counted
  and sized. Not the synopsis, not the tagline, not poster, backdrop or logo URLs, not the
  homepage or external ids, not per-file paths or track lists. `LibraryDetailDto` carries all
  of those because a detail *page* needs them; a model does not, and a series with nine seasons
  would spend the operator's context on artwork links it cannot see. A `verbose` argument adds
  the synopsis and per-source detail for the rare "what is actually in this file" question.

  **People are out entirely.** `/api/persons/{provider}/{id}` is not wrapped: an actor's
  biography is the clearest example of a payload that is large, always available elsewhere,
  and never what the operator was asking this server about.

## Verification steps

Unit tests cover the tool schemas, the window and empty-result contracts, and the argument
handling. What they cannot cover needs a Core-managed runtime:

1. Install the app with the dev runtime, start it through Core, and confirm the tools appear
   in `hosty mcp` — the fail-closed annotation filter means a missing `readOnlyHint` shows up
   as an absent tool rather than an error.
2. Drive each read tool from an agent and confirm the answers match the web UI for the same
   question, against a library large enough to truncate.
3. Take a real `NeedsReview` item through `list_ingest` → `get_ingest_item` →
   `search_metadata` → `match_ingest_item`, and confirm the item completes the pipeline.
4. Confirm a personal-state tool refuses when the caller carries no Hosty user, beside the
   same call succeeding when it does.
5. Confirm `scan_catalog` on an already-scanning catalog reports that rather than queueing a
   second scan, and that a scan of a catalog large enough to take minutes does not hold the
   tool call open — the failure this is meant to prevent only appears at that size.
6. Ask an agent for something to watch under a real constraint and confirm it reaches for
   `list_recommendations` rather than paging the library — the engine's ranking is the answer,
   and a tool set that invites the model to re-rank by hand has failed even when the answer
   looks reasonable.
7. Run each dictated scenario end to end against a real host, in the operator's own words
   rather than as tool calls — a surface that needs the question rephrased into its own
   vocabulary has not made it. In particular: ask about a download by the film's title and not
   by its release name, and ask about something that finished downloading but was never
   identified, which should be volunteered rather than have to be asked for.
8. Repair a multi-movie pack through `match_ingest_item` with more than one group, and an
   episode ingest with per-file season and episode numbers. A single-identity match passing
   says nothing about either.
