# MCP Tools — plan

Status: Draft
Created: 2026-09-01
Updated: 2026-09-01

M6 in [root](../../root.md): the media server's use cases exposed as MCP tools, so an
agent running on the host can answer *"do I have this?"*, *"why has this not shown up?"*,
*"get me this"*, and repair a bad identification — without the operator opening the web UI.

Hosty already carries the plumbing. An app declares `interfaces.mcp` in its manifest with a
path, Core authenticates the caller and the app authorizes it, and the `hosty mcp` connector
re-exports the tools to whatever agent the operator is using. Nothing here needs new platform
work; this plan is about which tools exist and what they are allowed to claim.

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
  result must say which kind of nothing it is, or the agent will report absence as fact.
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
| `get_title` | `GET /api/library/{id}` and `/{id}/episodes` — seasons, files, sizes, user state |
| `list_shelf(kind: recent\|resume\|nextup)` | the three shelf routes |

### Why something is missing

The pipeline is where an agent earns its place: `NeedsReview` and `Failed` items are exactly
what an operator does not notice until they go looking for a film that never appeared.

| Tool | Backed by |
| --- | --- |
| `list_ingest(status?, stage?)` | a **new** filtered, paged listing (see Deliverables) |
| `get_ingest_item` | `GET /api/ingest/{id}` — stage history and where it stopped |
| `get_server_status` | catalogs, active scans, `GET /api/vpn`, `/api/dht`, counts per ingest status |

### Acquiring

| Tool | Backed by |
| --- | --- |
| `search_metadata` | `POST /api/metadata/search` — resolves a title to a `providerRef` |
| `list_downloads` | `GET /api/torrents` |
| `add_torrent` | `POST /api/torrents/add` — not idempotent, answers "accepted" |
| `control_download(action: pause\|resume\|stop_seeding)` | the three control routes |

### Repairing an identification

| Tool | Backed by |
| --- | --- |
| `match_ingest_item(providerRef)` | `POST /api/ingest/{id}/match` |
| `advance_ingest_item(action: retry\|skip\|pin\|retarget)` | the remaining stage commands |

### Catalogs and space

| Tool | Backed by |
| --- | --- |
| `list_catalogs` | `/`, `/mounts` and `/usage` together, so "how much room is left" is one call |
| `scan_catalog` / `refresh_metadata` | the scan and refresh routes; both answer "accepted", both report a scan already running rather than starting a second |

### Personal state

| Tool | Backed by |
| --- | --- |
| `set_title_state(watched?, favorite?, rating?)` | six routes behind one call |
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

### Phase 1 — the two listings the tools need

The listings the MCP surface depends on today return everything they have. `GET /api/library`
filters only by `catalogId` and `kind`; `GET /api/ingest` takes no parameters at all. For an
agent both outcomes are bad in the same way — either the whole library lands in the model's
context, or it is cut silently and "not found" stops meaning anything.

- [ ] **A paged, searchable library listing.** Title substring search, plus the existing
      `catalogId`/`kind` filters and a watched-state filter, with `limit`/`offset` and a total
      count in the response. Additive: the existing call with no new parameters keeps
      returning what it returns today, so the web client is not broken by this change.
- [ ] **A filtered, paged ingest listing.** `status` and `stage` filters, same window shape.
- [ ] **Regenerate the OpenAPI document and the Apple client** so the new parameters reach
      `src/api/openapi` and the generated Swift client rather than drifting from it.

### Phase 2 — the MCP surface

- [ ] **`interfaces.mcp` in `manifest.json`** and a JSON-RPC endpoint answering `initialize`,
      `tools/list` and `tools/call`.
- [ ] **The read tools**: `search_library`, `get_title`, `list_shelf`, `list_ingest`,
      `get_ingest_item`, `get_server_status`, `list_downloads`, `list_catalogs`,
      `search_metadata`, `get_release_calendar`.
- [ ] **The write tools**: `add_torrent`, `control_download`, `match_ingest_item`,
      `advance_ingest_item`, `scan_catalog`, `refresh_metadata`, `set_title_state`,
      `manage_watchlist`.
- [ ] **The window contract on every list result** — `limit`, `returned`, `truncated`,
      reported from what actually ran rather than from what was asked for.
- [ ] **The empty-result contract** — a result with nothing in it says whether the catalog is
      unscanned, scanning, offline, or genuinely without a match.
- [ ] **Detached operations answer "accepted"**, with the note saying what was started.
- [ ] **User resolution** — the acting Hosty user is carried into every personal-state tool,
      and a call without one is refused rather than answered.

### Phase 3 — the skill

- [ ] **An app-provided skill** (`agent.skillFile` in the manifest). Hosty lets an app hand
      the agent a skill document, and this app needs one more than most: without the pipeline
      vocabulary — `Intake → Identify → Organize → Probe → Publish`, and what `NeedsReview`
      means — a model cannot tell from `list_ingest` whether it is looking at a problem or at
      normal progress.

## Open questions

- **Does `add_torrent` need an approval gate?** It commits disk and bandwidth on the
  operator's behalf. The Hosty connector can mark it non-read-only, which surfaces it as an
  approval in the agent's own client, but that is the client's policy rather than this app's.
  Recommendation: ship it non-read-only and rely on the client's approval, and revisit only if
  an operator reports an unwanted grab.
- **Should `get_server_status` include VPN state?** It is genuinely useful for "why is nothing
  downloading" and is also the most sensitive field in the app. Recommendation: include
  whether the VPN is up, never the endpoint or credentials.
- **How much of a title's detail does `get_title` return?** The full detail record includes
  every source file and track. Recommendation: summarize by default — sources counted and
  sized, not enumerated — and let a `verbose` argument ask for the rest, so a series with
  nine seasons does not fill the context.

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
   second scan.
