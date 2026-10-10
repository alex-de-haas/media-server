# MCP Tools live acceptance — 2026-10-10

Baseline: `1bcf0429df8472ef4618e7e13403e85260cd2b14`.
Scope: [MCP Tools](../features/mcp-tools/feature.md), deliverables D1–D9.
All eight live scenarios passed against an isolated Core-managed dev installation.

## Environment and method

Core installed and started Media Server from an isolated source copy with the baseline
implementation, HostySdk.App 0.8.0, its own database, and external fixture mounts.
The operator completed ordinary browser setup and sign-in. Core's account page issued
an app-scoped `mcp:read` token; existing delegated credentials exercised authorized
writes. Codex selected the tools from natural-language questions and sent real MCP
JSON-RPC requests. The Core `hosty mcp` bridge was checked separately over stdio.

Media consisted of self-generated two-second video clips. TMDb lookups, ffprobe,
identification, organization, sidecars, enrichment, and publication ran normally.
Thirty synthetic published titles exercised pagination. Two retained completed-download
records exercised status lookup and title-to-download linkage; this run did **not**
exercise BitTorrent peer transfer. Multi-file fixtures grouped the clips before retrying
the real identification stage. Authentication sessions were never inserted into storage.
The existing operator installation and library were not modified.

## Eight scenarios

| Acceptance | Deliverable | Observed result |
| --- | --- | --- |
| 1. Core discovery | D2 | Core-managed install/start succeeded. `hosty mcp` exported all 14 read tools, each with boolean `readOnlyHint: true`. Direct initialization returned 200; `notifications/initialized` returned 202, zero body bytes, and no content type. |
| 2. Read/UI parity and truncation | D3 | All 14 read tools matched their web UI data sources after comparing shared fields. Home, title detail, Activity, history calendar, and release calendar were also inspected in the browser. Library results reported 25 of 33 movies with `truncated: true`; offset 25 returned the final 8 with `truncated: false`. |
| 3. Single-item repair | D4 | A scanned unknown clip reached `NeedsReview`. `list_ingest` → `get_ingest_item` → `search_metadata` → `match_ingest_item` identified Big Buck Bunny (TMDb 10378). The pipeline reached `Publish` / `Done`, with all eight stages completed and `Big Buck Bunny (2008)/Big Buck Bunny (2008).mp4` published. |
| 4. Personal-state identity | D5 | With the authenticated Host user's empty app-account mapping removed from the disposable database, `list_watch_history` returned a personal-state tool error. After normal account synchronization/sign-in restored the mapping, the same call succeeded. It did not borrow another user's history. |
| 5. Minutes-long scan | D6 | A catalog of 1,200,000 hard-linked fixture clips returned `accepted` plus a job ID in 40 ms. A second call returned `already-running` in 2 ms. Status still reported `scanning: true` at 137.20 seconds; reads remained responsive. The fixture app was then deliberately stopped, and the small database restored. Full completion of this stress scan was not an acceptance requirement. |
| 6. Recommendation constraint | D7 | “Suggest something to watch; a movie, please, not a series.” selected `list_recommendations(kind: movie, limit: 5)`. The response preserved the engine's order and reasons, matching the UI feed. No library paging or manual ranking was used. |
| 7. Operator questions | D8 | The questions and observed answers below used the tool surface directly, including lookup by film title and volunteering a completed unidentified download. |
| 8. Multi-group and episode repair | D9 | One `match_ingest_item` call with two movie groups published Sintel (45745) and Tears of Steel (133701). Another used series 1437 with per-file S01E01 and S01E02 identities, publishing both under `Firefly (2002)/Season 01/`. Both ingests reached `Publish` / `Done` with all stages completed. |

The fourteen parity checks covered `get_server_status`, `search_library`, `get_title`,
`list_ingest`, `get_ingest_item`, `search_ingest_candidates`, `search_metadata`,
`list_downloads`, `list_catalogs`, `list_shelf`, `list_watch_history`,
`list_recommendations`, `get_release_calendar`, and `preview_release`. Comparison
used the same authenticated browser's BFF responses, preserving ordering where it
matters and comparing semantic projections where the UI carries additional fields.

## Operator-language observations

- “Do I have a film about a dragon?” used server status and
  `search_library(kind: movie, about: dragon)` and found Sintel.
- “Has Big Buck Bunny downloaded yet?” used `list_ingest(title: Big Buck Bunny)`
  and followed its `downloadId` to `qa-bbb-release.2160p.fixture`, which is not the
  film's title. The answer reported 100% and publication, and also volunteered the
  completed Elephants Dream fixture still at `Identify` / `NeedsReview`.
- “What did I watch yesterday?” used `list_watch_history` for October 9–10 in the
  host timezone. It returned Big Buck Bunny at 20:00 Amsterdam time and explicitly
  excluded one undated play, matching the history calendar.
- The movie-only recommendation question preserved the engine's order, starting with
  The Nut Job 2: Nutty by Nature, Bear-Night-Mare, and Oasis.
- “When was Sintel released?” checked the tracked release calendar and provider
  preview; both reported September 30, 2010.

## Scoped-token enforcement and automated checks

D1 also passed live checks: the scoped administrator saw only fourteen reads, and
all eight guessed write-tool names returned `isError: true` before dispatch. After
the token was revoked through Core's ordinary account page, the next `tools/list`
request returned HTTP 401 in 28 ms; immediately beforehand it returned HTTP 200.

The focused MCP suite passed 82 tests. Regression coverage includes exact scopes,
assistant `mcp:invoke`, per-request revocation, missing and malformed read-only
annotations, 401/403/503 behavior, cancellation, and the empty notification response.
The full API suite and CI results are recorded in the accompanying PR. An initial
full local run hit the pre-existing scan/version-deletion SQLite race assertion;
all six isolated cases and subsequent full runs passed without unrelated code changes.

Acceptance records contain no credentials. The disposable read token was revoked;
temporary browser and runtime artifacts are kept outside the repository.
