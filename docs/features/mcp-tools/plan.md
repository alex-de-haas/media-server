---
status: Ready
created: 2026-09-01
updated: 2026-10-10
summary: Scoped MCP read access and eight live acceptance scenarios through a Core-managed runtime.
components: [src/api]
---

# MCP Tools — scoped access and live acceptance

## Approval and scope

The owner approved completion in chat on 2026-10-10: support `mcp:read`, run all eight
live scenarios, and deliver one PR through CI and review to a regular merge commit.
This replaces the unresolved `mcp:write` proposal: Core does not issue that scope.
The existing tools are documented in [feature.md](feature.md).

## Target behavior

- Preserve locally validated legacy delegated credentials. For other bearers, use the
  SDK's MCP-specific introspection against Core on every request, without a cache.
  This also preserves support for Core's distinct assistant MCP credentials.
- Require `mcp:read` for the online path. Only an assistant credential carrying
  `mcp:invoke` may mutate; an external scoped token stays read-only even for an admin.
  Read-only discovery omits write tools, and guessed write calls are refused before
  dispatch. A missing or malformed read-only annotation never grants read access.
- Preserve the existing app-user and administrator checks. An authenticated Host user
  without an app account cannot read or change personal state on someone else's behalf.
- Missing or inactive credentials receive 401, insufficient scope receives 403,
  and unavailable or unreadable Core introspection receives 503. Pass the invoked tool
  name to Core for audit without logging the bearer. Keep notification acknowledgements
  at HTTP 202 with an empty body and no content type.
- Validate against a disposable Core-managed dev environment, isolated from the
  operator's library. Record actual observations for every acceptance scenario;
  automated tests do not replace these checks.

## Deliverables

- [ ] D1. Accept scoped `mcp:read` tokens with uncached MCP introspection, fail-closed
  discovery and invocation, preserved delegated permissions, and regression coverage.
- [ ] D2. Acceptance 1: install/start through Core and discover the annotated read tools
  through `hosty mcp`.
- [ ] D3. Acceptance 2: invoke every read tool as an agent, compare with the web UI,
  and demonstrate explicit truncation on a sufficiently large library.
- [ ] D4. Acceptance 3: repair a real NeedsReview item through the four-tool workflow
  and observe successful pipeline completion.
- [ ] D5. Acceptance 4: observe personal-state refusal without an app account and
  success for an authenticated caller with one.
- [ ] D6. Acceptance 5: observe prompt scan acceptance on a minutes-long catalog scan
  and refusal to queue a duplicate while it is running.
- [ ] D7. Acceptance 6: answer a natural-language viewing constraint with the engine's
  `list_recommendations`, without hand-ranking library pages.
- [ ] D8. Acceptance 7: execute the operator-language scenarios, including download
  lookup by film title and volunteering a completed but unidentified download.
- [ ] D9. Acceptance 8: repair a multi-movie pack with multiple groups and an episode
  ingest with per-file season/episode numbers, observing their published results.

## Delivery and verification

Run targeted authentication, authorization, protocol, and tool regressions, then the
API build and full test suite and documentation validation. Bump only the runtime app
version for the new capability. Update reality documentation and delete this plan in
the same PR only after D1–D9 are complete. Include acceptance evidence in the PR, pass
CI and review on the final head, and merge with a regular merge commit.

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
