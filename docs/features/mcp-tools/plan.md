# MCP Tools — plan

Status: In Progress
Created: 2026-09-01
Updated: 2026-09-03

> Every deliverable this plan originally defined has shipped — see [feature.md](feature.md). One new
> one is below, and the rest of what is left cannot be asserted in a unit test: the project has no
> integration harness, and those questions are about what an agent does with the tools, which only a
> running agent answers.

## Deliverables

- [ ] **Accept scoped access tokens on `/api/mcp`.** A delegated token is what the AI Gateway carries
      and what the surface authenticates today; a scoped token is what an *external* agent client
      keeps in its own configuration, introspected against Core on every call rather than verified
      locally — which is what lets revoking it take effect at once. `HostyScopedTokenClient` arrived
      with SDK 0.6.0, so the mechanism exists. What does not is the decision this turns on: which
      scopes gate which tools, and whether a token without `mcp:write` sees the write tools refused
      or absent. Absent is the safer default and the harder one to explain, so it needs deciding
      rather than defaulting.

## Verification that needs a running host

Against a Core-managed dev runtime:

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
