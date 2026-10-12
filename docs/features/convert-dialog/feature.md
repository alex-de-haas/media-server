---
created: 2026-07-29
updated: 2026-10-12
summary: Fixed-selection Remux and Convert confirmations with track names, video encoding controls and optional Dolby Vision rewriting.
---

# Remux and Convert Dialogs

Movie and episode Management use the same compact track table. The operator selects
tracks from one container and separate audio/subtitle files from any version of
the same movie or episode there, then chooses **Remux** or
**Convert** beneath the table header. Both require exactly one embedded video track
and at least one audio track; subtitles are optional. Neither dialog changes the
selection. Closing preserves it; successful submission clears it. Each opening
starts with fresh names and operation settings. A refresh that changes selected
track facts closes the confirmation rather than submitting stale data.

Both dialogs list the selected video, audio, subtitles and separate files with
editable names. They show a read-only MKV output summary, and leave original files
untouched. There are no language, default-track or copy/encode mode controls.
Sequential [Join parts](../video-part-joining/feature.md) remains separate.

Both confirmations include a required **Version name**, initially `Remux` or
`Convert`. The name becomes the new version's label and the suffix of its MKV
filename beside the input, using the title's normal naming convention. It does not
change when encoding settings change. Names are trimmed, limited to 120 characters
and reject filename separators/control characters. Name and path collisions with
existing versions, files or active jobs appear beside the field; editing the name
clears that error without losing track names or settings. API clients that omit
`versionName` retain automatic naming.

## Remux

Remux copies all selected tracks. On a selected Dolby Vision profile 7 video in
Matroska, it offers **Convert Dolby Vision to profile 8.1**, initially off. This
requires the engine's Dolby Vision tooling capability. The explanation states that
the HDR10 base layer is copied, RPU metadata is rewritten and the enhancement layer
is discarded. Other profiles and unknown profiles do not get a rewrite option.
The selected video's index, metadata and edited name survive the rewrite pipeline.

## Convert

Convert always re-encodes the selected video. Its controls are:

- Codec: HEVC or H.264.
- Resolution: original, 2160p, 1080p, 720p or 480p; the API prevents upscaling.
- Quality: highest, high (default), balanced or small.
- Encoder: Auto, VAAPI or Software; hardware fallback remains the engine's job.

A quality level travels to every encoder; it is not a software-only CRF. The engine
maps it to its selected encoder's rate control. The selected video's dynamic-range
warning distinguishes Dolby Vision profiles, HDR10+ and unknown profile data.
Dolby Vision profile rewriting is offered only by Remux.

Selected embedded audio tracks can optionally become E-AC-3, at 640 kbps above
stereo, 256 for stereo and 128 for mono. Rows explain the 7.1-to-5.1 downmix and
Atmos/DTS:X loss when applicable. Separate audio remains copied. The API retains
its legacy audio-only re-encode contract, but these two UI operations do not expose
copy-video plus re-encoded audio.

The size estimate uses only selected tracks and their recorded bitrates. Encoding
applies the quality share and target-resolution ratio to the selected video.
Missing track bitrate or duration yields an unavailable estimate; container
bitrate is never substituted. Audio re-encode estimates use the target bitrate.
The UI describes the result as approximate because content determines actual size.

## Request and compatibility

The grid always sends explicit `videoStreamIndex`, `audioStreamIndexes`,
`subtitleStreamIndexes` and separate-file IDs. An empty list means no tracks of that
type. No default override is invented when the selected subset omits a default.
Only changed names travel in `metadataEdits`, including a cleared name as an empty
title. Languages and existing default/forced flags are retained. Metadata edits on
excluded, foreign or unselected tracks are refused.

`GET /api/transcode/availability` reports `explicitVideoSelection`, read from the
engine's `GET /hardware`. Older engines report false. The UI explains the required
update, and the API refuses explicit selection rather than letting an old engine
silently pick its first video. Extraction keeps its existing engine contract.
Legacy API requests without explicit video selection retain their previous behavior.

The API checks video ownership/type, explicit audio/subtitle lists, move locks,
selected-video height and Dolby Vision eligibility. The engine probes the actual
file to verify stream indexes and types. Both retain output conflict protection;
errors stay in the confirmation with the selected tracks and edited names intact.
The normal output importer probes the new file and persists its facts.

Languages remain supported by the API, normalized by `LanguageTags`; the new
dialogs edit names only. See [stream titles](../stream-title-editing/feature.md),
[sidecars](../external-track-sidecars/feature.md) and
[extraction](../track-extraction/feature.md).

## Testing Expectations

- `track-actions.test.ts`: exact selections, eligibility, ownership, stale IDs,
  name edits, copy/encode payloads, selected audio settings and size estimates.
- `detail.spec.ts`: keyboard selection, collapse persistence, one-container scope,
  fixed dialogs, video/sidecar names, fresh defaults, errors, movie/episode parity,
  codec/resolution/quality/encoder controls and capability/Dolby Vision gates.
- `TranscodeServiceConversionTests`, `StreamMetadataEditTests` and
  `RemoteTranscodeEngineWireTests`: source ownership, video selection, selected
  metadata, selected resolution, old-engine refusal and exact wire names. Custom
  version names survive job reload and output import; conflicting labels, paths,
  existing files and active jobs cannot be overwritten. Movie and episode names
  retain their normal title prefix without stacking the input version's suffix.
- `TranscodeServiceTests`, `LanguageTagsTests` and `format.test.ts`: output naming,
  legacy API behavior, language normalization and profile-specific warnings.
- Transcode Engine tests verify remux/encode mapping and metadata, selected-video
  probing, Dolby Vision stage selection/title/dispositions and synthesized output.
