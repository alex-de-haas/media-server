# Blu-ray Import and MKV Creation

Status: In Progress
Created: 2026-09-24
Updated: 2026-09-25

## Goal

Download or scan a BDMV directory into the library as a durable Blu-ray source,
then let an administrator explicitly create an MKV version with their chosen
playlist and tracks. Import completes without waiting for conversion. The disc
itself cannot be played through Media Server; other versions of the same title
remain available normally.

The user approved the movie-only, menu-free workflow and required UHD and Dolby
Vision support on 2026-09-24. Media Server integrates with Transcode Engine;
underlying media tools are the engine's implementation concern. Real-disc
verification can follow implementation and is not a prerequisite to starting it,
but remains an unchecked acceptance deliverable until performed.

## Baseline and required changes

The implementation started from the following file-only baseline:

- [Torrent ingest](../torrents-and-organizer/feature.md) and catalog import work
  with individual files. `DownloadFileService` selects video and companion files;
  `LibraryImportService` recursively enumerates video files. `.m2ts` is recognized,
  but the enclosing disc and its `.mpls` playlists are not. Discovery must group a
  disc before individual-file filtering, and suppress its internal clips as
  independent library candidates.
- Organize and probe assume files, and staging cleanup discards leftovers. They
  must retain the complete owned disc structure, move it as a unit, and publish a
  non-playable source without passing its directory to the ordinary file probe.
- `MediaSource` currently describes one file with one duration and stream list.
  A disc needs an explicit source kind and playlist-specific inspection; it must
  not borrow an arbitrary clip's duration or tracks.
- [Convert](../convert-dialog/feature.md) already creates a new version with
  explicit track selection. Reuse its controls and job presentation, adding disc
  inspection and playlist selection before the track-selection step.
- [Native playback](../native-playback/feature.md) and
  [Jellyfin compatibility](../jellyfin-compatibility/feature.md) must exclude disc
  sources from playable candidates, including direct stream and remux endpoints.

## Target behavior

### First-release boundary

Support unencrypted BDMV directory releases in movie catalogs, from torrents and
catalog scans, with one selected playlist producing one MKV per operation. Multiple
discs and existing file versions can belong to the same movie. A second operation
can produce a different edition or track selection without overwriting the first.
The MKV contains the selected playlist's primary video plus the selected audio
and subtitle tracks. Menus, interactive content and secondary picture-in-picture
video are not included.

UHD Blu-ray and Dolby Vision are mandatory first-release requirements, alongside
ordinary 1080p Blu-ray. The default copy operation must preserve resolution,
bit depth, HDR signalling and Dolby Vision data, including the enhancement layer
where present; it must not silently produce HDR10-only output or discard part of
the selected primary picture. Dolby Vision auxiliary data/layers are part of that
picture, not the excluded picture-in-picture content. Any explicit transformation,
such as the existing profile 8.1 conversion, must describe its changes separately
and never stand in for lossless preservation without the user's choice.

ISO images, physical drives, DVD/VIDEO_TS, decryption, disc menus/BD-J, stereoscopic
3D/MVC, interactive playback and automatic conversion are outside this proposal.
The user explicitly approved blocking BDMV in series/anime catalogs for the first
release. Reject a known BDMV selection before starting its download in these
catalogs; when a magnet reveals its structure later, stop its automatic import
with a clear unsupported-content reason. Catalog scans apply the same restriction.
Never fall back to importing the disc's internal clips as episodes, and preserve
already downloaded data for explicit operator handling. Multi-film disc splitting
is also outside the first release; ambiguous assignments require review.

"Unencrypted" means that the media payload can be read without disc-protection
decryption; it does not mean that menus or the normal BDMV directory structure
have been removed. A folder name or torrent file list alone is not a conclusive
encryption test. Disc inspection must distinguish a required decryption step from
missing or corrupt data. Decryption is not part of the proposed engine capability;
an encrypted source must receive a specific explanation instead of starting a
conversion that cannot read its media.

Automatic conversion at download time is not a deliverable. The manual operation
owns its settings independently of an ingest or download row.

### Download, discovery and organization

1. Detect disc structure from torrent metadata when available and confirm it on
   disk after download. Magnet metadata follows the same detection path.
2. Treat the disc as one selectable import unit, including its playlists, clips,
   metadata and auxiliary directories. Keep member torrent-file indexes so file
   selection cannot accidentally download only the largest `.m2ts`.
3. Identify the movie from its release/disc folder and confirmed metadata, using
   the existing review flow for ambiguous matches. Disc discovery does not infer
   an episode or movie from internal names such as `00001.m2ts`.
4. Organize into an exclusively owned directory, for example
   `Movies/Title (Year)/Title (Year) - Blu-ray/BDMV/...`, preserving relative paths
   and accompanying disc directories such as `CERTIFICATE`. Separate disc roots
   in a multi-disc release remain separate sources with collision-safe names.
   Loose companion tracks remain governed by the existing sidecar workflow.
5. Publish the source and title metadata. Disc inspection can be pending or
   unavailable without holding an otherwise valid import in the pipeline.

Preserve the existing seeding policy: an item with `keepSeeding` remains in the
download stage until seeding stops. A completed library import does not require a
conversion job, even when the conversion engine is absent.

Discovery must also handle a catalog containing `BDMV` directly, without treating
the entire catalog or a shared movie folder as the owned disc root. Move only
identified disc members into a dedicated directory. Do not claim unrelated files
as disc members. Suppress recursive clip discovery even for a recognized but
incomplete disc, and surface a structural error instead.

Persist ownership and move progress before staging cleanup. Retrying after a
restart must reconcile the same source and paths. Never clean up the only copy
of members that have not been moved successfully. Repeated scans must find the
disc once, leave its structure intact, and avoid re-importing job outputs.

### Source model and availability

Add an explicit file/disc discriminator to ingest candidates and library sources,
with migrations defaulting existing rows to file sources. Store the owned disc
root and member identity needed for moves, change detection and torrent selection.
Directory size is the aggregate of owned members, not the size of one clip.

Keep three facts distinct: the source exists, it can be inspected/converted, and
it can be played. A valid stored disc has `requires_conversion` playback status;
missing members, an unsupported disc and an unavailable engine have their own
inspection/conversion explanations. Unknown duration and media characteristics
must remain unknown until a playlist is inspected, rather than displaying zero
duration or implying a disc-wide set of tracks.

Persist or cache playlist inspection against a disc revision. The inspection
contains stable playlist identifiers, duration, chapters, clip sequence and track
descriptions. Stream selections are scoped to a playlist, not global file probe
indexes. A changed disc invalidates inspection and is revalidated before a job.

### Library and client experience

- Web detail and Media views show the Blu-ray source, size, and "Requires
  conversion". Administrators get "Create MKV"; other users get an explanation.
  If inspection is unavailable, explain the dependency and allow a later retry.
- If only disc sources exist, the title remains browsable but Play is unavailable.
  If a file version exists, normal playback and source selection continue to work.
  Recommendation, preview and resume actions must not bypass this rule.
- Native playback returns an unsupported result with a stable conversion-required
  reason and no media URL for disc sources. Update the Apple client's reason
  presentation and generated contract so it explains that preparation happens in
  the web interface; no Apple conversion editor is part of this proposal.
- Jellyfin playback information exposes only eligible file sources. Keep the title
  browsable where compatible with clients, but never advertise BDMV direct play or
  provide a directory/clip URL as a substitute. Verify BDMV-only behavior in Infuse;
  if it cannot represent an unavailable title, document and implement filtering of
  BDMV-only titles on that compatibility surface.

Playback availability is derived per source and client, not a permanent
"unplayable movie" flag. A newly created MKV still goes through ordinary codec
compatibility and remux-index readiness checks; MKV creation does not guarantee
immediate playback on every device.

### Create MKV

The administrator opens the operation from a disc source:

1. Inspect and choose the playlist. Show duration, chapter count and available
   track summaries, with a suggested main feature only when justified. The longest
   playlist is a hint, not an authoritative selection. Preserve access to alternate
   cuts and explain ambiguous candidates. A playlist change clears incompatible
   track selections.
2. Choose audio and subtitle tracks, their names/languages and default/forced
   flags using the existing conversion controls. Keep original video and copy
   selected tracks by default. Preserve playlist chapters in the MKV. Require at
   least one supported video and audio track for this movie workflow.
3. Offer existing re-encode or Dolby Vision conversion options only where the
   engine explicitly supports them for disc input. State any loss before submit.
   Do not silently remove unsupported selected tracks or HDR metadata.
4. Confirm the output version name, destination and available-space assessment.
   Label estimates as estimates; absent per-track bitrates do not justify invented
   precise sizes. Output belongs beside the owned disc directory, outside it.
5. Run in the existing conversion activity list with progress, cancellation,
   actionable errors and explicit retry. Closing the dialog does not cancel work.

Keep the original disc after success, cancellation and failure. Offer ordinary
explicit source deletion after success, with the disc directory and its size
clearly identified. Creating an MKV must never opt the user into deleting BDMV.

### Engine dependency and job lifecycle

Extend the separate `transcode-engine` app; Media Server continues to ship without
local ffmpeg. The exact routes and capability field names are to be pinned in the
contract deliverable, not treated as existing APIs.

Transcode Engine is the integration boundary selected by the user, not a mandate
to use FFmpeg alone. Media Server submits inspection and conversion requests to
the engine and consumes its capabilities and results. The engine owns its FFmpeg,
ffprobe, libbluray and any necessary auxiliary-tool integration; the web/API must
not invoke those tools directly or acquire a parallel disc-conversion backend.

The required contract covers:

- Capability discovery for disc inspection, MKV creation and supported disc-input
  transformations, with clear handling of absent/older engines.
- Inspection by media mount label and relative disc path; playlist metadata and
  per-playlist stable track identifiers; structural/encryption/unsupported errors.
- Job submission with disc revision, playlist, selected tracks, transformations,
  target MKV path and an idempotency key. Resolve only server-owned mount paths;
  the client does not supply arbitrary paths or command-line options.
- Progress, cancellation, terminal result and durable reconciliation using the
  existing job infrastructure. Persist requested settings and input/output
  reservations before submission. A lost response must not create a duplicate job.

Implement through the engine's media tooling, then verify its reader/muxer path
with representative discs. FFmpeg's
[Blu-ray protocol](https://ffmpeg.org/ffmpeg-protocols.html#bluray) supports playlist
input, but that alone does not prove chapter, timing, PGS, lossless-audio or UHD
Dolby Vision preservation. Real-disc results may require engine changes or
auxiliary tools; they must not silently narrow the mandatory UHD/Dolby Vision
scope. Record the verified matrix and reject combinations the implementation
cannot yet preserve with an actionable explanation, keeping the corresponding
acceptance deliverable open. Do not reuse the existing two-file Join operation:
it does not interpret disc playlists or their segment boundaries.

Use hidden temporary output on the destination filesystem, validate it, then
publish with a no-overwrite rename. Verify duration against the selected playlist,
selected streams and flags, chapters, and promised HDR/audio properties. Cover
clip joins and seeking, not merely a successful probe or process exit.

Import the validated MKV once as a new source of the same movie, without identify
or a new title. Preserve history, metadata and any existing playable preference;
make the new file the preferred file source when none existed. Trigger the normal
remux indexing path. Persist completion independently of output import so a
restart or temporary probe/database failure retries import without rerunning the
conversion. Do not report full success until validation and import succeed.

Active jobs protect the entire disc tree and output reservation against rename,
remap, source/title/catalog deletion and catalog moves. Release protection only
after a confirmed safe terminal state. Restart and cancellation cleanup may remove
job-owned temporary output, never the disc or another job's file.

## Interactions with existing plans

- [File and directory management](../file-directory-management/feature.md) needs
  directory-aware operations for a disc source. Its
  [open last-source policy](../file-directory-management/plan.md) remains separate:
  removing an MKV while BDMV remains is not last-source removal. The title stays
  present and returns to conversion-required availability. Do not silently decide
  the general zero-source policy in this feature.
- [Catalog maintenance](../catalog-maintenance/feature.md) must distinguish a
  missing disc from an unavailable catalog mount, check owned members, and avoid
  producing tombstones merely because a directory fails `File.Exists`.
- [Remux streaming](../remux-streaming/feature.md) applies to the output MKV. Its
  [plan](../remux-streaming/plan.md) prohibits creating a second full file as a
  playback side effect; the explicitly requested, retained MKV here is a library
  operation. BDMV playback is not an extension of that virtual MP4 mechanism.
- [Media probing](../media-probe-providers/feature.md) keeps ordinary file probing.
  Disc inspection is a separate capability, with no false header-probe fallback.
  The [chapter-storage plan](../media-probe-providers/plan.md) owns general library
  chapter persistence; this feature preserves chapters in the output and inspection
  response without duplicating that database/UI deliverable.
- [External tracks](../external-track-sidecars/feature.md),
  [track extraction](../track-extraction/feature.md) and
  [part joining](../video-part-joining/feature.md) must not accidentally accept a
  directory as a file. Existing operations remain available on the resulting MKV.

## Deliverables and phases

These phases form one complete feature PR in Media Server. The engine dependency
ships through its own coordinated feature PR and independent release.

### Phase 1 — contract and engine implementation

- [x] Define disc ownership, inspect/create schemas, capabilities, playlist/track
  identity and job recovery in the [feature contract](feature.md). The companion
  engine plan is `transcode-engine/docs/features/bluray-import/plan.md`.
- [ ] Validate protection diagnostics against protected and damaged samples; do not
  infer encryption merely from a folder name or a generic probe failure.
- [ ] Implement UHD/HDR and Dolby Vision preservation through Transcode Engine,
  including the distinction between copying the original picture and an explicitly
  selected profile conversion. Cover metadata/layer handling with automated tests;
  real-disc acceptance follows in Phase 4.
- [x] Implement engine inspection, MKV jobs, no-overwrite publication, cancellation,
  restart handling, changed-input checks and output validation.
- [ ] Exercise cancellation and publication interruption during actual disc jobs.

### Phase 2 — durable disc sources and ingest

- [ ] Integrate directory copying with retained torrent placement so BDMV can be
  published while seeding. The current safety guard requires stopping seeding
  before moving a disc; file-based retained placement remains unchanged.

- [x] Add source-kind/member/inspection persistence and backwards-compatible
  migrations; keep existing file-source behavior and data intact.
- [ ] Group BDMV during torrent selection and catalog discovery; retain complete
  structure, identify the parent release, suppress clips and handle incomplete or
  out-of-scope discs explicitly, including blocking series/anime BDMV at known
  torrent selection, later magnet detection and catalog scan.
- [x] Implement directory organization, restart reconciliation and cleanup ordering;
  publish valid discs independently of conversion or engine availability.
- [ ] Extend scan, refresh, rename, move, deletion and job admission protections to
  the owned disc tree, including offline catalogs and paths containing symlinks.

### Phase 3 — preparation and playback availability

- [x] Add library source availability and the web Create MKV workflow, including
  inspection retry, playlist selection, track settings and dependency errors.
- [x] Integrate durable job submission, progress, cancellation, output validation,
  idempotent import, naming collisions, preference handling and remux indexing.
- [x] Guard direct/remux/playback resolution and update native/OpenAPI/Swift
  conversion-required reason handling.
- [ ] Verify the Jellyfin/Infuse policy on the actual client for disc-only titles.
- [x] Add backend xUnit tests with Imposter, web browser coverage and Apple
  availability/reason tests; exercise the workflow with synthetic and mocked
  fixtures without waiting for representative real discs.

### Phase 4 — real-disc verification, release and documentation

- [ ] Obtain representative 1080p, multi-clip, UHD/HDR and UHD Dolby Vision discs
  and run the end-to-end acceptance matrix. Verify playlist timing, chapters,
  selected tracks, HDR signalling and Dolby Vision metadata/enhancement-layer
  preservation on the actual MKV output; record tool versions and results. The
  user-supplied Battlefield Earth file list and BDInfo are a 1080p scenario
  reference, not a downloaded fixture or proof of runtime compatibility.
- [ ] Resolve failures discovered by real-disc verification in the engine and
  integration. Do not mark mandatory UHD/Dolby Vision support complete based only
  on successful job submission, a process exit code or ordinary 1080p tests.
- [x] Document implemented source, job and playback behavior and Testing
  Expectations; cross-link affected ingest, native playback and file management.
- [ ] Reconcile documentation with real-disc acceptance evidence before release.
- [x] Prepare independent versions: Media Server 0.84.0 → 0.85.0, Transcode
  Engine 0.11.0 → 0.12.0 and Apple client 0.15.0 → 0.16.0.
- [ ] Record real-disc validation evidence and confirm coordinated engine 0.12.0
  deployment before release; refresh the version baseline if intervening changes ship.
- [ ] Complete every deliverable, delete this plan and regenerate the index in the
  completing PR. Use one feature PR per repository, regular merge commits, and PR
  sections Summary, Changes, Verification, deliverables and version outcome.

## Open questions

None blocking implementation. The user settled the scope and integration boundary
in discussion. Representative-disc acquisition and verification remain explicit
Phase 4 deliverables rather than prerequisites for Ready. Any proposed reduction
of the mandatory UHD/Dolby Vision scope requires explicit user agreement.

## Verification steps

Automated verification uses the commands below. Real-disc acceptance remains
separate and unchecked; no actual disc payload accompanies the supplied BDInfo.
The coordinated engine work is in the original `transcode-engine` checkout used
by the Hosty dev runtime, with its own `docs/features/bluray-import/plan.md`.

For implementation:

- Run `dotnet build src/api/MediaServer.sln --configuration Release` and
  `dotnet test src/api/MediaServer.sln --configuration Release --no-build`.
- Run `pnpm -C src/web lint`, `pnpm -C src/web test`,
  `pnpm -C src/web build` and `pnpm -C src/web exec playwright test`.
- Regenerate native OpenAPI and the Swift client with
  `scripts/generate-apple-client.sh`; run the applicable MediaKit tests and Apple
  client build, recording exact commands and device/simulator used.
- Run the engine repository's required build/tests and real fixture jobs, recording
  tool versions, duration tolerances and preservation results.
- Exercise torrent and scan imports of a single-clip disc, a multi-clip playlist,
  alternate cuts, duplicate candidates, multiple discs, a disc directly under a
  catalog root, an incomplete disc and an ordinary `.m2ts` outside a disc.
- Verify that series/anime catalogs reject BDMV from torrent metadata, delayed
  magnet metadata and scans, preserve existing downloaded data and never create
  episode sources from internal disc clips.
- Verify track/default/forced selection, chapters, timing and seek behavior in the
  output. Include PGS and lossless/object audio in the supported track matrix.
  UHD/HDR and Dolby Vision preservation are mandatory acceptance cases, including
  the enhancement layer where present and separately tested explicit profile
  conversion. Refused inputs must explain why; refusal is not completion of a
  mandatory preservation case.
- Verify disc-only and mixed disc/file titles in web, Apple and Jellyfin/Infuse;
  viewer/admin roles; an absent or old engine; and a ready MKV with unsupported
  client codecs. No disc path may produce a playback URL.
- Interrupt moves, submission, engine processing and output import; retry and
  restart both services. Confirm one owned disc, at most one imported source per
  output, preserved originals and no visible temporary media.
- Test insufficient disk space, changed/missing members, output collisions,
  concurrent mutations, cancellation and deletion of the last MKV while its disc
  remains. Re-scan after each lifecycle transition to detect duplicate imports.
- Finish with `node scripts/docs-index.mjs --check` and `git diff --check`;
  report skipped verification and unresolved deliverables explicitly.


## Automated verification record — 2026-09-24

- Media Server Release solution build passes. The full xUnit run passes 1,996
  tests; subsequent focused runs pass 74 integration tests and 65 path/lifecycle
  tests after the additional changes. Existing analyzer warnings remain.
- Transcode Engine Release tests pass 357 tests with `FFMPEG_PATH` and
  `FFPROBE_PATH` set to installed tools, including real existing FFmpeg fixtures.
  Blu-ray-specific tests use synthetic playlists and mocked media facts.
- Web: TypeScript, ESLint, 156 Vitest tests, production build and all 143
  Playwright scenarios pass, including both new Blu-ray scenarios.
- Native OpenAPI and Swift code generation pass. All 215 MediaKit tests pass;
  the MediaServerTV Debug build for the tvOS simulator passes with signing off.
- Documentation indexes and `git diff --check` pass in both repositories.
- At that verification run, real BDMV/UHD/Dolby Vision payloads and a live Infuse client were not supplied;
  their acceptance deliverables remain unchecked. No deployment or commit is
  performed by these checks.

## PR verification record — 2026-09-25

The PR integrates current `main`, including retained torrent placement and the
Apple signing/cache work. The original repository folders remain the dev sources.
Media Server versions 0.84.0 → 0.85.0 and Apple versions 0.15.0 → 0.16.0 replace
the earlier baselines. Transcode Engine remains 0.11.0 → 0.12.0.

- Release solution build and the complete API xUnit run: 2,027 passed. After adding
  the retained-disc guard regression cases, all 19 organizer tests passed.
- Web frozen dependency install, ESLint, 162 Vitest tests, production build and all
  150 Playwright scenarios passed.
- Native Swift regeneration passed; 246 MediaKit tests and the unsigned tvOS
  simulator Debug build passed.
- Manifest validation, documentation index and diff whitespace checks passed.
- Transcode Engine Release build and all 360 tests passed. The companion PR is
  [Transcode Engine #42](https://github.com/alex-de-haas/transcode-engine/pull/42).
- Real-disc inspection established that Maximka playlists 00005 and 00013 are
  byte-identical. A chapter failure exposed MKVToolNix's removal of a short final
  mark; the engine now explicitly retains it, with regression tests. These checks
  do not establish complete real-disc or Dolby Vision acceptance.

The user authorized merging the reviewed first implementation on 2026-09-25.
The remaining unchecked deliverables, including directory copying while seeding,
protection diagnostics, lifecycle/client acceptance and full UHD/Dolby Vision
payload validation, remain open; merging does not establish acceptance of them.


Review verification on 2026-09-25: the Release solution build and all 2,038 API
xUnit tests pass, including corrupt saved selections, owned-member moves and
configured-root symlink scans. ESLint, all 162 Vitest tests, the production build
and all four Blu-ray Playwright scenarios pass. Browser regressions cover Enter
and direct form submission without required video/audio tracks. Apple sources
are unchanged by these review fixes; the prior 246-test/client-build results stand.
