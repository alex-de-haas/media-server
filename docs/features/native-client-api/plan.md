# Native Client API — plan

Status: In Progress
Created: 2026-08-02
Updated: 2026-09-08

> Every deliverable this plan defined has shipped — see [feature.md](feature.md).
> All that is left is verification that needs a running instance, which no unit
> test can stand in for. Work that turned out to belong to other features moved to
> them: chapters and probe provenance to [media probe
> providers](../media-probe-providers/plan.md), Swift client generation to
> [apple-client](../apple-client/plan.md).

## Verification that needs a running instance

None of these can be asserted in a unit test: the project has no integration
harness, and `WebApplicationFactory` would not supply one for the first two —
a `TestServer` has no real ports, so it cannot exercise a check that keys off which
binding a request arrived on.

- [x] **The public binding under real ports** — `/native/v1/server/public` answers,
      `/api/torrents` 404s there and still works internally. **Confirmed on
      2026-09-08** against the production host, the outward halves from off the
      host and the internal one from a probe on it; the evidence is recorded below.
- [x] **Middleware ordering** — the allowlist runs after routing and before
      authentication. **Confirmed on 2026-09-08** against the production host; the
      evidence is recorded below.
- [ ] **Route parity** — each native route returns what its `/api` twin does. They
      call the same service, which is visible in the code but not asserted against
      two live responses.
- [ ] **The realtime stream for a non-BFF caller** — `/native/v1/events` is mapped
      but has only ever been consumed through the web proxy. Partly established on
      2026-09-08: off the published binding the route answers 401 rather than 404,
      so it is routed and published outside the proxy. That events actually arrive
      there still needs a credential.

### Evidence — production run, 2026-09-08

Core 0.97.3, `com.haas.media-server` 0.72.3 on the docker runtime, published
binding `https://media.zayats.io`. Every request below was unauthenticated and
read-only.

| Request | Result |
| --- | --- |
| `/native/v1/server/public` | 200, pairing document naming `https://media.zayats.io` |
| `/native/v1/server` | 401 |
| `/native/v1/events` | 401 |
| `/native/v1/sync` | 401 |
| `/native/v1/definitely-not-a-route-xyz` | 404 |
| `/api/torrents` | 404, empty body |
| `/api/library` | 404, empty body |
| `/System/Info/Public` | 200 — the Jellyfin surface still answers |

On the host itself, against the loopback `api` binding `http://127.0.0.1:32261`:

| Request | Result |
| --- | --- |
| `/api/torrents` | 401 |

That one request is what separates "not published here" from "broken": the same path
answers 404 off the published binding and 401 off the internal one, so internally the
allowlist admits it and only authentication refuses. It is also the check a
`TestServer` cannot stand in for, since the two answers differ by nothing except which
binding the request arrived on.

The app's own log carries exactly three `HostyAuthenticationHandler[12]
AuthenticationScheme: Hosty was challenged.` entries for that run, one per
allowlisted route, and none for either `/api/*` request. Authentication therefore
ran only where the allowlist had already admitted the request, which puts the
allowlist before it. The allowlist also separates a real native route (401) from a
bogus one under the same prefix (404), so it decides on the matched route rather
than on a path prefix, which puts it after routing. Both halves of the ordering
claim hold.

What this run could not reach: anything needing a credential — route parity and a live
event stream both do.

## Verification steps

Against a Core-managed dev runtime:

1. Pair end to end: request a device code from Core, approve it in Shell's Access
   tokens tab, exchange the credential for an app identity token, and call
   `/native/v1/server` with it. Then unassign the user in Core and confirm the next
   request fails.
2. Confirm the public binding and the middleware ordering together, by calling both
   a published and an unpublished route on each binding.
3. Sync from an empty cursor, mutate the library (edit, delete a watched item,
   delete an untouched one, delete a catalog), sync again, and confirm the client
   view converges — including the purge and the catalog delete, the two cases that
   leave nothing behind to poll.
4. Play a title through a signed URL, including a sidecar dub, and confirm ranged
   requests and seeking work across the whole file.
5. Hold `/native/v1/events` open as a native caller and confirm events arrive
   outside the BFF proxy.
6. Confirm Infuse still browses, plays and syncs state throughout — the Jellyfin
   surface must not regress.
