# Apple Playback Buffering

Status: On Hold
Created: 2026-09-22
Updated: 2026-09-23

## Goal and approval

The user approved a client-only disk-cache experiment on 2026-09-22, with a tvOS
build to test against a film that reliably stalls. This explicitly replaces the
broader investigation draft: exact sample-table time mapping, preloaded test
harnesses, exported traces and alternative MP4 packaging are outside this first
experiment. The user deferred packaging investigation until after the device run.

The user subsequently approved a cache enable switch and a RAM/disk selector,
with the existing direct AVPlayer path preserved when disabled. RAM remains the
default for fresh installs and legacy preferences. Device testing is deferred
while the user observes the existing RAM version on tvOS 27.

The user also approved common diagnostics with caching disabled, memory caching or
disk caching, plus a more compact overlay. This resumes implementation only; the
physical-device cache experiment remains deferred.

## Target behavior

Offer the remux loader's 128 MiB RAM window or disposable session block files.
Keep existing range delivery, reader tracking, AVKit and recovery. Retain roughly
five minutes ahead and one minute behind the inferred reader position, estimated
from virtual resource length and duration, capped at 6 GiB with a 2 GiB free-space
reserve. Before duration is known, use a small byte target. Report time estimates
as approximate: VBR and sidecar layouts prevent exact time coverage.

Keep individual disk reads, HTTP fills and outstanding separate requests bounded.
Disk access runs on the loader queue, never the main actor. On cache creation,
read/write or space failures, release the session cache and resume with the old
bounded RAM window. Recoveries reuse the session; track changes create a new one.
Clean abandoned session files on first cache use after process launch.

Add on-screen cache mode/size, estimated coverage, real network receive counters,
cache delivery, pending-request coverage/age and cache I/O failure diagnostics.
Preserve those figures at the last stall and last recovery for a photograph.
Do not restore the previously harmful forward-buffer preference.

Settings expose Enable cache under Playback and show Memory / Disk only while
enabled. Preserve the legacy enable flag and save the selected storage even when
caching is disabled. Capture both choices when playback starts and preserve them
across track changes and recovery. The disabled path remains direct AVPlayer
network loading, with no additional cache or loader recovery.

Read native HTTP media request counts and transferred bytes from AVPlayer access
logs when no loader is present; use loader network counters when it is. Show
request frequency and aggregate bytes per request with explicit source/scope and
unknown values. Preserve the plain AVPlayer loading path. Merge contiguous loaded
time ranges before estimating buffer coverage. Compact common playback/network
metrics into one panel and show cache-only details separately.

## Deliverables

- [x] Bounded session disk window, cleanup, space reserve and RAM fallback.
- [x] Loader integration with estimated retention, bounded I/O, short-seek reuse
      and cancellation; no server/API changes.
- [x] On-screen diagnostics and retained stall/recovery snapshots.
- [x] Storage and loader regression tests, complete MediaKit test run and unsigned
      tvOS device build; bump only the Apple client version.
- [x] Update current feature documentation and generated index.
- [x] Cache enable/storage preferences, legacy migration, all three playback paths,
      accurate RAM/disk/fallback labels, tests and tvOS build.
- [x] Common diagnostics, native request counters, compact overlay, regression
      coverage and unsigned tvOS build.
- [ ] User device test: reproduce the failing film, record client version and
      diagnostic snapshots, and establish whether freezes/recoveries persist.

## Verification

Tests cover block boundaries, trimming/restarts, cleanup, missing/truncated files,
low storage, disk delivery and recovery/fallback without corrupting byte offsets.
Existing reader and playback tests must continue to pass. Build with
`xcodebuild -project src/apple/MediaServerTV.xcodeproj -scheme MediaServerTV -destination 'generic/platform=tvOS' CODE_SIGNING_ALLOWED=NO build`.

Implementation is verified; the user has deferred installation and physical-device
testing while observing playback on tvOS 27. The device test remains open until
the user reports the result. A
successful build and unit tests do not establish that the original freeze is fixed.

Verified again on 2026-09-23 in the isolated pull-request checkout:

- `swift test --package-path src/apple/MediaKit --disable-automatic-resolution`:
  245 tests passed in 42 suites, including common diagnostics, native/loader
  transitions, disk storage/fallback and existing playback coverage.
- The unsigned tvOS device build above succeeded; Apple client version is
  0.14.1 → 0.15.0. The runtime manifest is unchanged.
- `node scripts/docs-index.mjs --check` and `git diff --check`: passed.
- No signing, installation, physical-device playback or on-device layout check was
  performed; the user has deferred installation. The
  user's device run is the sole remaining deliverable for this experiment.
