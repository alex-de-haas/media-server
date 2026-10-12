---
created: 2026-07-27
updated: 2026-10-12
summary: Edit selected video, audio and subtitle names while producing a new version; language edits remain available through the API.
---

# Stream Title Editing

The [Remux and Convert confirmations](../convert-dialog/feature.md) show an editable
name for every selected track, including video and separate audio/subtitle files.
Track selection belongs to the Management grid. The confirmations have no language
or default-track controls. A new operation starts with the source names again.

Only changed names are sent in `metadataEdits`. An untouched name keeps its source
tag; an empty changed title clears it. Embedded tracks map to input zero and their
absolute index. A merged sidecar maps to its additional-input ordinal and stream
zero. A video name requires explicit `videoStreamIndex` and the engine capability
that applies it during copy, encode and Dolby Vision rewriting.

Edits naming foreign or excluded tracks are refused, including unmerged sidecars
and an embedded video other than the selected one. An edit must contain a title or
language. The API continues to accept language corrections, normalizing ISO 639-1,
ISO 639-2 and BCP-47 primary language tags with `LanguageTags`; unknown input is
refused. The UI leaves existing language tags unchanged.

There is no in-place rename. Metadata travels with the job and is written into the
new output. `TranscodeOutputImporter` probes that output and stores what the file
reports; neither the original file nor its database stream rows are relabeled.

## Testing Expectations

- `StreamMetadataEditTests`: embedded and sidecar addressing, excluded/foreign
  tracks, selected video names, empty edits and language normalization.
- `TranscodeServiceConversionTests` and `RemoteTranscodeEngineWireTests`: explicit
  video and edited names reach the engine without changing legacy requests.
- `track-actions.test.ts` and `detail.spec.ts`: only changed selected names travel,
  names include video and sidecars, and errors retain the form.
- Engine argument, endpoint and real ffmpeg tests verify output title changes,
  empty-title clearing and Dolby Vision final mux metadata.
