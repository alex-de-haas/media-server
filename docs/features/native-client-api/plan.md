# Native Client API — plan

Status: In Progress
Created: 2026-08-02
Updated: 2026-08-04

> Every phase this plan defined has shipped — see [feature.md](feature.md). What
> is left is one design question the implementation raised, and verification that
> needs a running instance. Work that turned out to belong to other features has
> moved to them: chapters and probe provenance to [media probe
> providers](../media-probe-providers/plan.md), Swift client generation to
> [apple-client](../apple-client/plan.md).

## The one open decision

- [ ] **Where artwork comes from.** The detail projection hands out the provider's
      own URLs (`ImageAsset.RemotePath`, i.e. TMDb), which the web UI has always
      used and which work today. Serving artwork from our local cache instead
      (`ImageAsset.LocalPath`, which the Jellyfin surface already reads) would keep
      a LAN-only client working without internet and stop TMDb seeing what a user
      browses — at the cost of our bandwidth and cache for something a CDN does
      better. It is a decision, not an oversight, and it wants answering before the
      client's browsing surface is built rather than after.

## Verification that needs a running instance

None of these can be asserted in a unit test: the project has no integration
harness, and `WebApplicationFactory` would not supply one for the first two —
a `TestServer` has no real ports, so it cannot exercise a check that keys off which
binding a request arrived on.

- [ ] **The public binding under real ports** — `/native/v1/server/public` answers,
      `/api/torrents` 404s there and still works internally.
- [ ] **Middleware ordering** — the allowlist runs after routing and before
      authentication.
- [ ] **Route parity** — each native route returns what its `/api` twin does. They
      call the same service, which is visible in the code but not asserted against
      two live responses.
- [ ] **The realtime stream for a non-BFF caller** — `/native/v1/events` is mapped
      but has only ever been consumed through the web proxy.

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
