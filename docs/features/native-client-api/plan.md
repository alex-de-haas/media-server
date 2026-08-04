# Native Client API — plan

Status: In Progress
Created: 2026-08-02
Updated: 2026-08-04

> The surface itself has shipped — see [feature.md](feature.md). What remains here
> is work that could not be finished with it, each with the reason.

## Remaining deliverables

### Fields the data model cannot supply yet

Two things this plan promised the item DTO would carry turned out to have nothing
behind them. Rather than inventing them, they are recorded as work in the probe
subsystem:

- [ ] **Chapters.** There is no chapter table, column or probe output anywhere in
      the schema, so the DTO carries none. A client cannot offer chapter
      navigation until [media probe providers](../media-probe-providers/feature.md)
      records them.
- [ ] **Probe provenance.** Which provider answered a probe — the external engine
      or the container-header reader — is not persisted, so the DTO cannot say it.
      Without it a thin stream list reads as a broken file rather than as "the
      header reader answered this", which was the whole point of surfacing it.

### Images come from the provider, not from us

- [ ] **Serve artwork from the local cache.** The detail projection hands out the
      provider's own URLs (`ImageAsset.RemotePath`, i.e. TMDb), so a client fetches
      posters from the internet even when the server is on the same LAN, and its
      browsing is visible to TMDb. Artwork is already cached locally
      (`ImageAsset.LocalPath`, which the Jellyfin surface serves); a native image
      route over that cache needs its own signed URL and an allowlist entry.

### Verification that needs a running instance

- [ ] **Route parity with the web counterparts.** Each native route calls the same
      service its `/api` twin does, which is visible in the code but not asserted
      against two live responses. The project has no integration-test harness, and
      adding `WebApplicationFactory` would not help the neighbouring case — a
      `TestServer` has no real ports, so it cannot exercise the public binding.
      Verify against a Core-managed dev runtime instead.
- [ ] **Middleware ordering.** That the allowlist runs after routing and before
      authentication is true in `Program.cs` and pinned by nothing. Same
      verification path.
- [ ] **The realtime stream for a non-BFF caller.** `/native/v1/events` is mapped
      but has only ever been consumed through the web proxy.

### Belongs to the client

- [ ] **Swift client generation.** Proving the document generates a client needs
      the SwiftPM package that consumes it, so it moves to `apple-client-core`.
      The server-side half of the guarantee — a committed document that CI diffs —
      is done.

## Verification steps

Against a Core-managed dev runtime, since none of these can be asserted in a unit
test:

1. Pair end to end: request a device code from Core, approve it in Shell's Access
   tokens tab, exchange the credential for an app identity token, and call
   `/native/v1/server` with it. Then unassign the user in Core and confirm the next
   request fails.
2. Confirm the public binding: `/native/v1/server/public` answers without a token,
   while `/api/torrents` 404s there and still works on the internal binding.
3. Sync from an empty cursor, mutate the library (edit, delete a watched item,
   delete an untouched one, delete a catalog), sync again, and confirm the client
   view converges — including the purge, the case tombstones do not cover.
4. Play a title through a signed URL, including a sidecar dub, and confirm ranged
   requests and seeking work for the whole file.
5. Hold `/native/v1/events` open as a native caller and confirm events arrive
   outside the BFF proxy.
6. Confirm Infuse still browses, plays and syncs state throughout — the Jellyfin
   surface must not regress.
