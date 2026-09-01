# MCP Tools

Created: 2026-09-01
Updated: 2026-09-01

This server's use cases as MCP tools, so an agent on the host can answer *"do I have this?"*,
*"why has this not appeared?"*, *"get me this"*, and repair a bad identification without the
operator opening the web UI. `interfaces.mcp` points at `/api/mcp`; `agent.skillFile` hands the
agent [the skill](../../agent.md) that teaches it this server's vocabulary.

## Authenticated Like Everything Else

`/api/mcp` sits behind the same scheme as the rest of `/api`. Core answers "who is this" and this
app answers "what may they do" — an MCP endpoint with an identity system of its own would be a
second answer to the first question, and the one more likely to be wrong. The acting Hosty user is
resolved per call, and the tools that touch personal state refuse without one rather than answering
for nobody: for nobody, every title is unwatched, which reads as a fact about the library.

## Twenty-One Tools, Not Eighty Routes

The HTTP surface has roughly eighty routes. Tools are shaped by the question an operator asked, not
by the route that answers it — `list_shelf` covers three rails, `set_title_state` covers six routes,
`control_download` covers three. Thirteen read, eight write.

Two pairs look interchangeable and are not, so each description points at its sibling:

- `get_release_calendar` reads the watchlist and answers nothing for a title nobody tracks, which is
  the case "when does it come out" is usually about. `preview_release` asks the provider directly and
  records nothing.
- `search_library` answers what this server holds; `search_metadata` asks the provider by name.

## What A "No" From This Server Means

Every rule here exists because a plausible implementation would let an agent state something false
about this host.

- **Every list reports its window** — `limit`, `offset`, `returned`, `total`, `truncated`. Without
  it "there are no failures" can mean "none among the rows I was handed". `truncated` is *exact*
  rather than inferred from a full page: these queries count before they cut, so the number left
  behind is known, and the usual full-page heuristic marks a complete last page as truncated.
- **An empty search says which kind of nothing it is.** A catalog nothing has scanned holds files
  this server knows nothing about, so "not in the library" and "not on the disk" are different
  statements. The note fires only when it applies — one attached to every empty answer would train
  the model to skip it.
- **Detached work answers `accepted`, not done.** A queued scan, a metadata refresh, a torrent add.
  A scan already under way is reported as such rather than counted as a second start, and an unknown
  catalog says so rather than accepting work that was never going to happen.
- **An unknown filter value is refused, not dropped.** Accepted and ignored, "nothing is failing"
  comes back as a list of everything.

## Annotations Are A Consent Prompt

Read tools declare `readOnlyHint: true`, write tools declare `false`, through separate helpers so
read-only is never a default a write tool inherits by forgetting to override it. The two mistakes
fail differently: a read tool missing the hint is not exported at all, because the Hosty connector's
filter is fail-closed, while a write tool claiming it is exported *and* shown to the operator as
safe.

**Nothing declares itself destructive, because nothing here removes anything.** Deleting a title, a
season, an episode, or a download with its files is irreversible and this app has no undo, so an
agent mistaking one id for another erases the wrong series. `skip`, which discards files from an
ingest, is excluded for the same reason. `pin` is absent on different grounds: it takes a whole
provider identity where `advance_ingest_item`'s other actions take none, and folding it in would
give one tool argument shapes that share nothing.

## Testing Expectations

- **Both annotation directions.** Every read tool declares itself read-only and every write tool
  declares that it is not, asserted as one pairing over the whole set — and both classes are checked
  to be non-empty, or the assertion holds vacuously for whichever is missing.
- **Nothing destructive**, asserted across every tool, so adding a delete fails a test rather than
  passing unnoticed.
- **The window in both directions**: a truncated page says so and a complete last page does not.
  Inferring truncation from a full page is the mutation that separates them.
- **The empty-result note in both directions**: present when a catalog is unscanned, absent when
  none is.
- **Refusals beside acceptances** — an unknown filter value, personal state without a user, a match
  naming no source file, an already-running scan, and a scan of a catalog that does not exist.
- **The skill against the tools.** Every tool name the skill mentions is asserted to exist: the skill
  is prose the model reads before deciding anything, so a stale name sends it to a dead end and
  nothing else notices.
- **Not verified live.** No agent has called these tools through a running Core, and the remaining
  checks that need one are tracked in [plan.md](plan.md).
