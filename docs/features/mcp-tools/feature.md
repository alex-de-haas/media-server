# MCP Tools

Created: 2026-09-01
Updated: 2026-09-04

This server's use cases as MCP tools, so an agent on the host can answer *"do I have this?"*,
*"why has this not appeared?"*, *"get me this"*, and repair a bad identification without the
operator opening the web UI. `agent.skillFile` hands the agent [the skill](../../agent.md) that
teaches it this server's vocabulary.

## How Core Finds The Surface

`interfaces.mcp` declares the path `/api/mcp` **and the endpoint it hangs off** — `api`, the API
service's HTTP port. That endpoint exists for this reason: the app publishes `ui` and `jellyfin`, and
neither is the API. It is deliberately not public, unlike the reference app's equivalent: this is the
admin-capable surface, and publishing it to widen who can reach a tool is the opposite of the intent.

**The endpoint name is load-bearing and fails quietly.** Core resolves it by exact key, then by a
`.key` suffix, and then by *the first public endpoint* — so a name matching nothing does not error,
it answers with whatever is published first. That was this app's `ui`, the Next.js service, which has
no `/api/mcp` at all: it proxies under `/api/proxy/[...path]`. The result was a surface that started
clean, passed every test, and could not be called. Both halves of the reference are asserted, because
nothing in the build or the runtime says a word about either.

## Authenticated By The Credential Agents Actually Carry

`/api/mcp` authenticates a **delegated token** — the short-TTL credential Core signs for this app when
an agent calls on an operator's behalf — and nothing else. Core still answers "who is this" and this
app still answers "what may they do"; the app builds no identity system of its own.

**It does not use the scheme in front of every other route, and that distinction is the whole
section.** The identity scheme revalidates an *app identity token* against Core, and Core rejects a
delegated one outright because the credential type is inside the signed input. Authenticating this
route the ordinary way therefore refused every agent call with a 401 while browser traffic kept
working — which made a wrong scheme look like a configuration problem for as long as it took to read
the two credentials apart. Validation is local, against the key Core injects as
`HOSTY_DELEGATED_TOKEN_PUBLIC_KEY`, so it costs no round trip.

The acting Hosty user is resolved to this app's own account the same way the identity scheme resolves
it. A Host user with no account here is **authenticated with no account** — a third state, not an
error: the tools that touch personal state refuse it rather than answering for nobody, because for
nobody every title is unwatched, which reads as a fact about the library. Administrator is read from
the token's Host role, since a delegated token never becomes a `ClaimsPrincipal` and has no claim to
carry the app's mapped role.

Scoped access tokens — the credential an external agent client keeps in its configuration — are not
accepted yet; that is tracked in [plan.md](plan.md).

## Twenty-Two Tools, Not Eighty Routes

The HTTP surface has roughly eighty routes. Tools are shaped by the question an operator asked, not
by the route that answers it — `list_shelf` covers three rails, `set_title_state` covers six routes,
`control_download` covers three. Fourteen read, eight write.

`list_watch_history` is the one tool with no single route behind it. The watched flag says a title was
finished at some point and carries no date, so it cannot answer "what did I watch last week"; the
calendar behind the web UI could, but only 62 days at a time — a number taken from the shape of a
month grid and, until this feature, the only thing bounding the scan. The bound moved to the rows, and
the period is free: "yesterday" and "five years ago" differ only in where the window sits.

Two pairs look interchangeable and are not, so each description points at its sibling:

- `get_release_calendar` reads the watchlist and answers nothing for a title nobody tracks, which is
  the case "when does it come out" is usually about. `preview_release` asks the provider directly and
  records nothing.
- `search_library` answers what this server holds; `search_metadata` asks the provider by name.

`add_torrent` takes a magnet link **or** a base64 `.torrent` file, exactly one. The file is the
better source when there is one: it carries the file list and sizes, so the free-space refusal
happens before the download starts rather than after metadata arrives from peers, and starting does
not depend on reaching a swarm to learn what the torrent is. The tool offered only magnets at first,
and an agent holding a `.torrent` converted it to a magnet to get through — a workaround that threw
away exactly those two properties.

**A refusal from the download service is a tool error, not a crash.** `TorrentRequestException`
derives from `Exception` and matched none of the invoker's catches, so every way that service says
no — unreadable base64, a file that is not a torrent, a missing catalog, not enough free space —
escaped as a 500 and ended the caller's turn. Accepting `.torrent` files widened it: the earlier
free-space refusal this feature exists to deliver was among the answers being lost.

## What A "No" From This Server Means

Every rule here exists because a plausible implementation would let an agent state something false
about this host.

- **Every list reports its window** — `limit`, `offset`, `returned`, `total`, `truncated`. Without
  it "there are no failures" can mean "none among the rows I was handed". `truncated` is *exact*
  rather than inferred from a full page: these queries count before they cut, so the number left
  behind is known, and the usual full-page heuristic marks a complete last page as truncated.
- **Undated plays are counted, never placed.** A play imported from a provider that reported no time
  carries none, and inventing one would put a film in a week it may not belong to. They can therefore
  never fall inside a period, so an answer about one says how many it could not include — "you watched
  nothing that week" and "nothing that week that carries a date" are different statements.
- **An empty search says which kind of nothing it is.** A catalog nothing has scanned holds files
  this server knows nothing about, so "not in the library" and "not on the disk" are different
  statements. The note fires only when it applies — one attached to every empty answer would train
  the model to skip it.
- **Detached work answers `accepted`, not done.** A queued scan, a metadata refresh, a torrent add.
  A scan already under way is reported as such rather than counted as a second start, and an unknown
  catalog says so rather than accepting work that was never going to happen.
- **An unknown filter value is refused, not dropped.** Accepted and ignored, "nothing is failing"
  comes back as a list of everything.

## The App Authorizes

`scan_catalog` and `refresh_metadata` have admin-only HTTP twins, and calling their coordinators
in-process would walk around that check entirely: the endpoint asks for an authenticated user and
nothing more, which is right for reading a library and wrong for maintenance. Those two tools are
gated on the caller's host role, and the list is a list rather than a mood — reading the library is
the ordinary case and is not an administrator action.

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
- **The admin gate in both directions** — a non-administrator refused, the same call as an
  administrator accepted, and an ordinary read left alone. A gate that refused everyone would pass
  the first assertion alone.
- **An episode match end to end.** The pipeline branches on `MediaKind.Episode` and sends every
  other kind through movie resolution, so a tool offering only movie and series resolves each episode
  file as a film — which succeeds and is wrong. Asserted by what the match creates, not by what it
  returns.
- **Scan state stamped by the scan**, with the offline case beside it: a volume that could not be
  read must not report a last-scanned time.
- **The caller, from the token alone**: an operator resolved to their account, a Host user with no
  account authenticated *without* one, an administrator told apart from an ordinary operator, and
  refusals for a token minted for another app, an expired one, a missing header, and an app identity
  token — the credential this route used to demand and the one an agent never has.
- **The manifest against itself**: every `interfaces.mcp` entry names a declared endpoint, and every
  endpoint names a service and port that exist. Removing the `api` endpoint reproduces the shipped
  defect, which is the point — nothing else in the build or the suite can see a reference that
  resolves to the wrong service.
- **The skill against the tools.** Every tool name the skill mentions is asserted to exist: the skill
  is prose the model reads before deciding anything, so a stale name sends it to a dead end and
  nothing else notices.
- **Not verified live.** No agent has called these tools through a running Core, and the remaining
  checks that need one are tracked in [plan.md](plan.md).
