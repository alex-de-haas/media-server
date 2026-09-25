# Blu-ray Sources and MKV Creation

Created: 2026-09-24
Updated: 2026-09-25

## Library sources

Movie imports group BDMV and CERTIFICATE members into one `Bluray` source before
ordinary media-file filtering. Torrent candidates retain their member indexes.
Internal clips are excluded from independent imports; standalone M2TS files remain
ordinary files. Series and anime catalogs refuse disc imports.

When a torrent retains active seeding, disc organization refuses to move its
source directory and asks the operator to stop seeding first. File-based retained
placement continues through the existing copy service.

Organization moves owned members into a dedicated, collision-free directory and
persists the destination before moving them. A retry resumes the same move.
Publishing checks basic structure without depending on Transcode Engine. Sources
store directory size and no invented disc-wide stream list or duration.

The source is visible with `RequiresConversion` availability. Native playback
returns `requires_conversion` without a URL; Apple explains preparation in the web
interface. Jellyfin playback candidates and direct media resolution exclude discs.
A file version retains its usual playback eligibility and default preference.

Rename, catalog move and deletion operate only on owned BDMV/CERTIFICATE
members and retain unrelated siblings. An empty old disc root is removed. Active MKV jobs reserve
the source against conflicting library mutations. Disc operations refuse symbolic
links in the owned tree or destination ancestry.

## Manual MKV creation

Administrators use Create MKV in the Media view. Inspection lists playlist IDs,
clip sequences, duration and chapter counts. The picker explains playlists,
sorts usable entries by descending duration, and puts entries within 10% of the
movie's metadata runtime first as possible main-film candidates. When none match
or metadata is absent, candidates fall back to entries within 90% of the longest
duration. This is explicitly a hint; multiple candidates show an ambiguity
notice. Durations include seconds. The selected entry shows chapters and segment
count, with the ordered disc filenames available in expandable details.
Selecting a playlist obtains its
tracks; changing playlists resets the track selection. Audio and subtitle controls
include language, title, default and forced flags. At least one audio track is
required. Subtitles are optional. The primary picture and selected tracks are
copied; menus and secondary picture-in-picture tracks are omitted.
The engine explicitly retains short final playlist chapters that MKVToolNix
otherwise removes by default. A chapter-count mismatch reports the expected and
actual counts and prevents publication.

Media Server calls Transcode Engine through `POST /bluray/inspect` and
`POST /jobs/bluray`. The `hardware.tools.blurayImport` capability requires
MKVToolNix 81 or newer. Media Server itself invokes no media executables.
Older or absent engines leave the disc importable and disable Create MKV.

Jobs persist their selection and stable client ID before submission. Uncertain
responses are reconciled using that same ID. Missing or corrupt stored selections
fail with an actionable request to inspect the disc and create a new job. The engine binds selection to a disc
revision, checks free space and writes to a temporary output. It checks tracks,
flags, titles, chapters, duration and probed picture characteristics before
no-overwrite publication. Available input Dolby Vision configuration records are
compared with the output, including enhancement-layer presence. This header check
is not evidence that all per-frame RPU or enhancement-layer payloads survived.

A completed output becomes a separate, idempotently imported MKV source. The
original disc remains after success, failure and cancellation. Existing conversion
and profile 8.1 operations apply to the resulting MKV; the disc operation copies
video. Engine restarts preserve job records and mark interrupted work failed.

## Testing Expectations

- xUnit tests cover torrent grouping, root-level discs, scan suppression, series
  restrictions, ownership-preserving moves/deletion, recovery of partially moved
  members, symbolic-link refusal, native playback refusal, stale inspection,
  durable submission and idempotent output import.
- Engine tests cover MPLS timing and chapters, malformed/multi-angle input,
  selected track mapping, mounted paths, revision changes, restart recovery and
  rejection of changed HDR/Dolby Vision configuration.
- Playwright covers unavailable-engine presentation, playlist/track submission
  and keyboard/form submission with missing required tracks.
  MediaKit tests cover the conversion-required reason and mixed-version selection.
- The [remaining acceptance plan](plan.md) includes actual 1080p, multi-clip, UHD/HDR
  and Dolby Vision output checks, Infuse compatibility and coordinated release.
  No representative disc payload is part of the automated fixtures.
