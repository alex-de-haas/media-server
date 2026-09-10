# Video Part Joining

Created: 2026-09-10
Updated: 2026-09-10

## Joining two parts of one movie

An administrator opens **Join parts** in a movie's Media tab, selects two versions
as **Part 1** and **Part 2**, and swaps them if necessary. The dialog shows their
filenames, durations and expected combined duration. The output is a new **Joined**
version in a Matroska file beside Part 1. Its unique filename contains the job id.
Both source files remain unchanged, as do the movie's preferred version, metadata,
favorites and watch history. Playback uses the existing version selector.

Exactly two distinct versions of the same published movie are accepted. Episode
joining, automatic part detection, editing overlaps and cross-title joins are not
supported. Existing sidecar **Merge** continues to add tracks to one video's
existing timeline; see [Convert](../convert-dialog/feature.md).

## Compatibility

The engine inspects both actual files before accepting the job. Tracks must
correspond in count and order, codec configuration, timing, video geometry, audio
format, language/title tags and default/forced flags. A mismatch names the track
and differing property. Nothing silently re-encodes or drops an incompatible track.

Supported stream types are SDR H.264, HEVC, MPEG-4 Part 2 and MPEG-2 video;
AAC, AC-3, MP3, FLAC and 16-bit PCM audio; SubRip, ASS/SSA and WebVTT subtitles;
and matching TrueType/OpenType font attachments. The strict configuration check
can reject differently encoded files even when their codec labels match. HDR,
Dolby Vision, high-bit-depth video, object-based audio, bitmap subtitles and other
unverified stream types are refused. Positive nonzero start timestamps are
normalized for Matroska inputs; other containers with such offsets are refused.

Embedded subtitles continue on the combined timeline. Chapters from Part 2 are
shifted by Part 1's duration, and font attachments are retained. External subtitles
and audio stay attached to their original parts: the dialog lists them as excluded
because their timing does not describe the full movie.

## Engine dependency and API

This requires **transcode-engine 0.10.0** with `videoPartJoining: true` in its
`GET /hardware` response. An older, unavailable or disconnected engine does not
offer the action. Media Server exposes the flag in `/api/transcode/availability`.
The API image continues to ship without ffmpeg.

`POST /api/transcode/join` takes `{ "sourceIds": [part1Id, part2Id] }` and uses the
same administrator authorization as Convert and Extract. It persists a `Join`
job before sending the ordered mounted paths to the engine's separate
`POST /jobs/join` endpoint. The Media Server job id is also the engine's stable
`clientJobId`; a lost response is retried with the same id and output path.

The job row records both ordered source ids and paths, expected duration from the
engine, cancellation intent and whether the output has been imported. The regular
conversion list presents it as **Join parts · 2 files**, with progress, cancellation
and errors.

## File protection and completion

A shared admission gate serializes joining with version rename, source/title
deletion and catalog-move admission. Persisted active join rows protect both inputs
until the engine confirms a terminal state; a completed join holds its reservation
until output import finishes. Another join of the same movie is refused while one
is active. Removing job history cannot discard an active reservation. Removing an
imported join's history does not delete the library version.

The engine writes hidden temporary output, checks duration and retained streams,
and publishes with a rename that refuses an existing destination. An existing
file, including one without a database row, is never overwritten by a join.
Cancellation and failure remove only temporary files. The importer probes the
finished file, checks duration and records the new version once by item/output
path. Missing or invalid output fails the job without changing the originals.

A Media Server restart recovers pending submissions and unimported completed jobs.
An unreachable engine keeps the reservation in place. The engine journals joining
status under its app data directory; completed status survives its restart, while
an interrupted join becomes Failed with a restart explanation and its temporary
files are removed. Retrying that failed operation is an explicit new join.

## Testing Expectations

- `VideoPartJoinServiceTests`: ordered request and durable reservation, invalid or
  missing/cross-title sources, engine availability, move conflicts, actual rename
  and delete service protections for both parts, lost-response recovery under the
  same id, cancellation and history-removal guards, idempotent import and missing
  output. Preferred source and original file bytes remain unchanged.
- `RemoteTranscodeEngineWireTests`: the distinct endpoint, ordered mounted paths,
  stable id and absence of conversion/sidecar settings.
- Engine `JoinEndpointTests` and `VideoPartJoinTests`: HTTP validation, path/order
  checks, incompatible configurations, filename escaping, collisions, queued
  cancellation, restart status/cleanup, and real ffmpeg joins with decoded video
  and audio equivalence, seeking, shifted subtitles/chapters, nonzero timestamps
  and font attachment byte preservation. The real fixtures require `FFMPEG_PATH`
  and `FFPROBE_PATH`.
- Web `video-part-joining.spec.ts`: dialog preview, distinct selection and swap,
  exact submitted order, compatibility errors, older-engine gating and job labels.
  Run `detail.spec.ts` for existing movie and episode media actions.
