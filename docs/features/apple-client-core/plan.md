# Apple Client Core — plan

Status: Draft
Created: 2026-08-10
Updated: 2026-08-10

> Phase 2 of the [Apple client](../apple-client/plan.md) epic, and the first
> release worth using. Every server half it needs is built and verified:
> [`native-client-api`](../native-client-api/feature.md) for browsing,
> [`native-playback`](../native-playback/feature.md) for negotiation,
> [`remux-streaming`](../remux-streaming/feature.md) for the container.
> The [foundations](../apple-client/plan.md#phase-01--foundations) — `src/apple/`,
> the capability profile, the escape hatches — are in place.

## Goal

A viewer picks up the remote, finds a film, presses play, and watches it in Dolby
Vision. Nothing before this delivers that: the server can already answer every
question the client will ask, and no client asks them.

## What exists, and what this adds

The server side is done, which makes this plan unusually well-specified — it is
written against a committed OpenAPI document rather than against an intention.

| Already there | What this feature adds |
| --- | --- |
| `GET /native/v1/server/public`, anonymous | The pairing screen that consumes it |
| Core's device authorization flow | Chaining it into an app identity token, and keeping it |
| `GET /native/v1/sync?cursor=` over a change log | The local mirror it feeds |
| `POST /native/v1/playback/resolve` | Sending a real capability profile and acting on the answer |
| `/native/v1/media/{id}/remux` with signed URLs | An `AVPlayer` pointed at one |
| `/native/v1/playback/sessions/*` | Progress reporting, resume, watched |

## Pairing, which the app must not invent

Hosty Core already gives a browserless client a full path into any runtime app, so
this app writes **no** authentication code of its own. The chain, verified against
`../docker-host`:

1. The viewer types the server address. `GET /native/v1/server/public` answers with
   the server name, the app id, the surface version and **`CorePublicOrigin`** —
   the one anonymous route, because needing a token to discover where tokens come
   from is a loop.
2. `POST {core}/api/auth/device/code` → an 8-character `userCode` in a
   lookalike-free alphabet, and a `verificationUri`. Both go on the television.
3. Someone approves it in Shell → Settings → **Access tokens**, from any device
   with a browser.
4. `POST {core}/api/auth/device/token`, polled no faster than the returned
   interval, collects a Core access token. An approved request is consumed on the
   first poll, so a replayed device code cannot mint a second credential.
5. `POST {core}/api/auth/apps/authorize` with that token as **bearer** → an
   authorization code. A bearer-presented Core session is deliberately CSRF-exempt,
   which is what makes this work with no browser.
6. `POST {core}/api/auth/apps/token` → an app identity token, audience-scoped to
   this app id, which the SDK's handler revalidates against Core on every request.

**The credential to be careful with is the one from step 4.** Core has no scopes,
so a Core access token carries its holder's full Core role — it can reach Core
itself and every other app. The app identity token cannot: it is audience-scoped,
and it is the only one that ever leaves the device on a request to us.

Lifetimes decide the shape of the storage: the app grant is 7 days idle / 30 days
absolute, while the Core access token is 90 days idle with **no** absolute expiry.
So the client re-runs steps 5–6 silently when the app token lapses, and only shows
the pairing screen again if the access token itself has gone.

## Deliverables

### Phase 1 — pairing

- [ ] **Server address entry**, with `GET /native/v1/server/public` as the check
      that something is there. Bonjour discovery is deliberately out: it fails
      exactly where this library lives — across subnets and through the tunnel —
      and typing an address once is not the friction worth solving first.
- [ ] **The device-code screen**: the `userCode` large enough to read across a
      room, the `verificationUri` beside it, the poll running while it is on
      screen and cancelled when it is not.
- [ ] **The whole chain to an app identity token**, steps 1–6 above, with the
      silent re-run of 5–6 when the app grant lapses.
- [ ] **Keychain storage**, both credentials, with the Core access token marked as
      the sensitive one. An unpaired app holds nothing.
- [ ] **The states that are not "signed in"**: a code that expired, an approval
      that was denied, a Core too old to have the device routes, a server that
      answers but is not a Media Server. Each says which it is.

### Phase 2 — the local mirror

- [ ] **A SQLite mirror fed by `GET /native/v1/sync?cursor=`**, so browsing costs
      no round trip. The cursor is opaque and carries the schema version, so a
      server that has moved on can order a reset rather than be guessed at.
- [ ] **The reset path**, exercised rather than assumed: a pruned cursor range must
      produce a clean re-snapshot and not a blind client.
- [ ] **Tombstones and purges**, which are the reason the change log exists — a
      purge leaves nothing behind and cannot be discovered any other way.

### Phase 3 — browsing

- [ ] **The library**, in the shape tvOS expects: a focus-driven grid, artwork from
      the signed image URLs, and the title's own editions rather than one row per
      file.
- [ ] **A title**, with its versions, its audio and subtitle tracks including the
      sidecar ones, and what the server says it can do with each.
- [ ] **Resume and watched**, read from the mirror so the grid does not wait on the
      network to show a progress bar.

### Phase 4 — playback

- [ ] **`POST /native/v1/playback/resolve`** with the real profile from
      `MediaKit.CapabilityProfile`, narrowed by the viewer's `PlaybackPreferences`.
- [ ] **`AVPlayerViewController`, not a custom player.** Recorded as a Phase 0
      deliverable of the epic and still the right answer: the transport bar, the
      track picker, the skip gestures and the Siri remote's whole vocabulary are
      free and cannot be reimplemented to the same standard.
- [ ] **Every refusal reason shown as itself.** The server answers
      `packaging_pending`, `packaging_unsupported_audio`,
      `packaging_unsupported_video`, `unsupported_dynamic_range` and the rest
      precisely so a client need not say "cannot play this". `packaging_pending`
      is the interesting one: it means *not yet*, the walk is coming, and retrying
      later works — so it is a state with a retry, not an error.
- [ ] **Sessions**: start, progress, stop, feeding the watch history the web client
      already shows.
- [ ] **Dolby Vision confirmed on hardware.** The simulator reports no
      HDR-eligible output and never will, so this is checked on the Apple TV 4K —
      the same way every measurement in the epic was taken.

### Phase 5 — the generated client

- [ ] **A Swift client generated from `src/api/openapi/MediaServer.Api_native.json`**,
      which CI already diffs on every build, so the two cannot drift. This
      deliverable belongs here because this feature owns the package that consumes
      it.
- [ ] **Decide generator or hand-written.** Not obvious: the document is 3050
      lines and generation kills drift, but a generated client is awkward to make
      pleasant at the call site and adds a build step to a project that
      deliberately has none. Whichever is chosen, the drift check must survive it.

### Closing the plan

- [ ] **`feature.md`** for this feature, created by the PR that ships the
      behaviour.
- [ ] **Epic deliverable** — check off "Constituent plans" in
      [`apple-client/plan.md`](../apple-client/plan.md) once this is Ready.
- [ ] **Index** — `node scripts/docs-index.mjs --fix`.
- [ ] **Version** — the client versions on `MARKETING_VERSION`, not
      `manifest.json`. Nothing here moves the server's version.

## Open questions

1. **Does the mirror earn its place in the first release?** Phase 2 is the largest
   piece here and browsing would work without it — call the API and show what comes
   back. The mirror pays off on a slow link and when the library is large, and the
   server side already exists, so it is cheap in server terms and expensive in
   client terms. The honest options are: build it now, or ship live calls first and
   add it when browsing actually feels slow. *Recommendation: live calls first.*
   The sync surface will still be there, and a mirror written against a real
   browsing screen will be a better mirror than one written against a guess.
2. **One profile per device, or per viewer?** `PlaybackPreferences` is currently a
   device-level thing. A household where one television is the shared one and
   another is in a bedroom might want them to differ, which the current shape
   already gives; but per-user track preferences live on the server
   (`/native/v1/playback/preferences`) and there are now two places a preference
   can live. Decide which wins before both exist.
3. **How much does the first release show?** The epic's Phase 6 owns
   recommendations, the calendar, people and the diary. This plan holds the line at
   browse-and-play, but "browse" for a television plausibly means a Top Shelf row
   and a "continue watching" shelf, and the second of those is nearly free from the
   mirror. Where the line falls is worth stating rather than discovering.
4. **What happens when the index is not built?** `packaging_pending` is a real
   state for a freshly added title, and on a slow disk the walk is minutes. A
   client can retry quietly, or say "preparing", or hide the title. Saying nothing
   and failing is the only clearly wrong answer.

## Verification steps

- `swift test` for everything in `MediaKit` that has no screen — the pairing state
  machine, the cursor handling, the resolve-response mapping.
- The whole pairing chain against a **real Core**, including the failure states,
  since none of them can be produced by a unit test.
- A film played end to end on the **Apple TV 4K**, in Dolby Vision, with a seek
  and a resume — the acceptance the epic already set for the server half, now with
  the client that will actually issue the requests.
- A title whose index has not been built, to see `packaging_pending` handled as a
  state rather than as a failure.
- A title with a sidecar dub and one with external subtitles, both of which the
  server carries and neither of which any other client of this library can play.
