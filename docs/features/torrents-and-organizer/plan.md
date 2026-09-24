# Publish While Seeding and Clean Media-Owned Staging

Status: In Progress
Created: 2026-09-23
Updated: 2026-09-24

## Owner direction and scope

On 2026-09-23 the owner clarified that Media Server owns each catalog's `.incoming`
directory and selects the download destination. This keeps staging on the same
filesystem as the eventual library file, including catalogs on different disks.
Torrent Engine remains a generic downloader: source plus destination in an available
mount, an info-hash handle, status and standard lifecycle operations. Do not change
its ownership model, introduce workspace allocation, add app identity, or build an
engine UI as part of this work. The earlier engine-ownership proposal is abandoned.

The owner proposes publishing media while its original torrent continues seeding:
copy for retained seeding, move otherwise. The owner approved this revised feature for implementation on 2026-09-23. Investigation of the original unreadable file remains
[On Hold](../ingest-move-recovery/plan.md); its cause was not established.

## Implementation baseline

The approved behavior is implemented in this worktree and documented in
[feature.md](feature.md). The remaining acceptance checks below must complete before
this plan is removed. Torrent Engine code and production installations are unchanged.

## Target behavior

### Existing ownership and mounts

Keep consumer-selected paths and mount bindings. Media Server owns staging creation,
retention, cleanup, canonical naming and file placement. It calls existing engine
operations for only its recorded torrents; it does not purge a global engine roster.
Use engine removal with data retained when Media Server performs filesystem cleanup.
No Torrent Engine API or destructive default change is required by this proposal.

### Download policy

Resolve the catalog default when adding a download, with an explicit user override.
Allow the operator to change Keep seeding while still downloading. Persist the
choice and serialize updates against completion/placement, taking one policy
snapshot when each Organize attempt begins. Do not switch transfer mode during an
active file operation. Enabling retained seeding after placement starts is rejected
with a clear reason. An Organize attempt parked for insufficient copy space can
explicitly switch to move through Stop seeding and continue; this is not blocked
by the normal policy cutoff. Stopping a published seed remains a separate action.

### Placement in Organize

Keep the existing dedicated Organize stage after Identify: naming requires resolved
identity. Make copy/move progress and failures visible there, not disguised as Probe.

Without retained seeding: await successful stop and removal of the registered
engine job with `deleteFiles=false` before moving source files. Preserve the info
hash and retryable operation state until that outcome is known. A timeout or failed
engine call is not permission to move/delete. Then move each required file into its
canonical destination, persist the result, and allow Probe only for successful
placements. Within one filesystem the intended fast path remains rename.

With retained seeding: after download completion, keep the engine job, complete
original torrent file tree and original paths intact. Copy the required media to
the library, then run Probe/enrichment/publication on the independent library copy.
Companion tracks must also be copied; samples, skipped files and other torrent
content remain in staging while seeding. No hardlinks: changes to the library must
not change bytes served by the torrent. Verify that completed data is readable via
the existing engine contract before beginning copy; do not infer readiness from a
preallocated length alone.

Copy into an operation-owned temporary destination, close the completed output,
verify transfer completion/expected byte count and rename into the reserved final
name. A failed/cancelled copy never becomes a probeable canonical file. Preserve
the source and safely resume/retry using recorded placement outcomes without
creating duplicate library versions or overwriting unrelated destinations. Byte
counts do not claim full integrity; acceptance tests compare fixture hashes.
Expose byte progress. Check the additional space required for remaining copies on
the destination volume at Organize time. Do not require an upfront extra-space
warning or confirmation when selecting Keep seeding; the normal download-space
check remains unchanged.

If space is insufficient, park Organize with a visible warning stating the required
and available space. Keep the source files and seeding intact, do not advance to
Probe, and do not burn through automatic retries for the same capacity shortage.
Offer exactly two recovery actions:

- Retry: recheck free space and retry copying, retaining seeding.
- Stop seeding and continue: persist the no-seeding intent, await successful engine
  stop/removal with files retained, then re-drive Organize using Move. Do not delete
  the staging source as part of stopping the torrent. If the engine operation fails,
  retain the files and show a retryable stop error; do not begin moving.

Free space can disappear after the check. A disk-full error during copy follows the
same parked-warning flow. Close and remove only this operation's incomplete output
before retry/fallback; if cleanup fails, expose that failure. Preserve completed,
recorded placements and process only remaining files, without creating duplicate
versions or overwriting unrelated files. In the fallback, retained originals for
already copied files become eligible for normal owned cleanup only after they are
no longer needed and the engine is released. Move can still fail (including for
filesystem metadata space); report that error at Organize rather than promising
that fallback always succeeds.

### Publication and seeding are independent states

Persist separate import and torrent-retention state. Successful import does not
remove the Download handle while seeding, and a seeding failure does not unpublish
an otherwise valid library item. Preserve original source paths separately from
canonical SourceFile/MediaSource paths so restart/cleanup still finds the seed tree.

Activity keeps a published seeding item visible with completed processing stages
and an explicit In library / Seeding status, upload information and retained space.
It is not shown as a failed or unfinished Probe. Stop seeding and remove originals
stops/removes the engine job first, then deletes only this owned staging root and
retained torrent metadata no longer needed by Media Server. Library files, playback
history and catalog entries remain untouched. Move the item to Done after teardown
succeeds; expose cleanup pending/failed and retry when it does not.

Stopping seeding during copy or before import finishes releases the engine but
must not delete files still needed by placement, review, Probe or companion work.
Serialize stop/cleanup with the import worker, retain originals until all required
placements finish, and do not restart the torrent on recovery after stop intent.
History clearing and generic Activity removal must not silently delete a published
library item or erase the last durable record of a live seed/failed cleanup.

### Cleanup owned by Media Server

Persist staging roots and pending cleanup independently of rewritten file paths
and retained Activity history. Retry transient deletion failures with bounded
backoff; expose exhausted errors and explicit retry. No-seed cancellation follows
the same engine-stop/remove-before-delete ordering and retains operation evidence.

Cleanup may run before Probe only when no required staged files remain. Companion
placement currently follows Probe, so cleanup cannot always precede Probe. Protect
roots used by downloads, seeds, review, incomplete placements or other ingests.
After successful placement remove known disposable leftovers; never recursively
sweep a root still holding required data. A directory-deletion failure does not
invalidate valid media, but it remains visible as pending cleanup.

Add a small administrator-only Temporary download files section to existing Media
Server settings: analyze owned roots, show their purpose, state, size and eligibility,
preview exact candidates, then clean selected. Revalidate ownership/activity and
paths at apply time. Report legacy unknown roots separately, without automatic
adoption/deletion based solely on age, names, size or absence from Activity. Current
active seeds are protected; ending a seed is the explicit Activity action above.

## Deliverables

- [x] Persist seeding policy, placement cutoff, original paths and retention lifecycle.
- [x] Implement safe Copy/Move placement, progress, capacity parking, independent
  companion copies and retry/fallback without duplicate completed versions.
- [x] Confirm engine release before move/deletion; retain failed/uncertain operation
  evidence and exclude stop intent from startup resume.
- [x] Publish while seeding and provide Activity stop/cleanup controls without
  deleting library data or erasing live ownership through history clearing.
- [x] Add durable cleanup with bounded retries and administrator settings analysis,
  selected-candidate preview/apply and protection of unknown or active roots.
- [x] Add backend and UI regression coverage, update feature documentation, and bump
  the runtime app from 0.83.0 to 0.84.0.
- [x] Verify the new admin API and UI/BFF using real Hosty identity in a separate
  Core-managed instance; run disposable filesystem regressions inside its Docker container.
- [x] Complete operator acceptance on the local development instance; the owner
  confirmed that the feature works on 2026-09-24 after testing the updated runtime.
- [ ] Complete real-engine acceptance with disposable media on a Windows/Docker
  fixture: validate live seeding during/after publication, stop during a long copy,
  locked-file cleanup retry and process restart, and playback of the copied file.
  Automated tests cover the filesystem and lifecycle boundaries with a controlled
  engine; no separate Windows fixture is available, and production fault injection
  remains excluded by the approved scope.
- [ ] After that acceptance passes, remove this plan and regenerate the docs index.

## Verification recorded on 2026-09-23

- `dotnet test src/api/MediaServer.Api.Tests --no-restore -p:OpenApiGenerateDocuments=false`:
  full API suite 2000 passed. After the final file-sharing adjustment, the API rebuilt
  and all 30 retained-download and organizer tests passed again.
- `npm test` in `src/web`: 156 passed; TypeScript checking passed.
- `npm run build -- --webpack`: production build passed. The default Turbopack
  build hit the environment's IPC port restriction; the first sandboxed build also
  could not fetch the existing Google Fonts dependencies.
- Playwright Activity, Settings and seeding-retention scenarios: 19 passed;
  all five directly affected scenarios passed again after the final UI changes.
- Core-managed Linux Docker: 20 lifecycle/filesystem tests passed, plus the later
  cancellation regression. The engine boundary is controlled in these tests.
- Real signed Hosty identity: direct admin analysis/apply returned 200, anonymous
  analysis returned 401, and UI/BFF to Docker API analysis returned 200.
- `node scripts/docs-index.mjs --check` and `git diff --check`: passed.

## Phases and verification

On 2026-09-24, the Activity policy indicator became a clickable toggle. The web
production build (`npm run build -- --webpack`) and all 156 web unit tests passed.
All seven seeding-retention Playwright scenarios passed, including enabling and
disabling seeding during download and pause using mouse and keyboard.

A local retained-seed run on 2026-09-24 exposed a single-file path mismatch between
the engine's add descriptor and registered `/files` response. The remote client now
uses only registered paths and refreshes them on idempotent add. All 40 affected
regressions and the full 2003-test API suite passed. The existing library copy and
retained original had identical SHA-256 hashes; a backed-up, explicitly identified
phantom database row was removed from the local test instance, after which Organize
completed with one source file and retained seeding enabled. A normal retry cleared
an interrupted Probe lease; engine-backed Probe, Sidecars, Enrich and Publish then
completed successfully while the original remained available for seeding.

One Media Server branch/PR covers policy/state, placement, lifecycle/cleanup and UI.
No separate engine change, ownership/auth project or engine frontend is included.

Build the API, run the full API test suite, build/test affected web flows and run
`node scripts/docs-index.mjs --check`. Tests cover policy/completion races, copy
failure/cancellation/out-of-space (both preflight and mid-copy), retry after space
is freed, copy-to-move fallback after partial success, failed engine stop during
fallback, restart while awaiting capacity, move failure preventing Probe, engine failures
and lost replies, restart, two catalogs on different disks, duplicate destinations,
sidecars/samples, pending review, cleanup locks and Activity/history actions.
Assert stop/remove precedes move/deletion and no operation affects another download.

Use disposable Core-managed Windows/Docker fixtures: verify no-seed rename and
cleanup; verify copy publication with original seeding still active; compare hashes;
stop seeding during and after import; verify cleanup retry after a locked file and
restart. Confirm the published copy remains playable and protected/unknown staging
is untouched. Do not run fault injection or cleanup on production media.

## Review verification on 2026-09-24

Review fixes scope mutation gates to one download, reject cancellation after placement,
return lifecycle conflicts as HTTP 409, preserve completed video stages on sidecar
capacity failures, and leave unowned occupied sidecars intact without blocking publication.
Activity restores the slow SSE fallback outside active retained-copy progress.

- `dotnet test src/api/MediaServer.Api.Tests --configuration Release --no-restore`:
  2015 passed; Release compilation succeeded and native OpenAPI is unchanged.
- `pnpm test`, `pnpm lint`, `pnpm build --webpack` in `src/web`: 156 unit tests
  passed, lint passed, and production build/TypeScript validation passed.
- Full Playwright suite against the production build on port 3199: 148 passed
  without retries. Tooltip re-entry waits for mutation completion, and navigation
  assertions wait for hydration. The API lifecycle test accepts repeated indexing
  progress events while still asserting the exact state-transition order.
- Manifest validation, docs index check and whitespace validation passed.

## Status and version outcome

Approved for implementation on 2026-09-23. Runtime verification uses disposable data;
production cleanup is not part of acceptance. The reported corruption is
not treated as a proven File.Move or torrent-engine defect.
