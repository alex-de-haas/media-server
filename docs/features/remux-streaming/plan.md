# Remux Streaming — plan

Status: Draft
Created: 2026-08-05
Updated: 2026-08-05

> Part of the [Apple client](../apple-client/plan.md) epic, and the last server
> piece before a client can play the library.
> [`native-playback`](../native-playback/feature.md) already answers `remux` for a
> source whose codecs a client can decode and whose container it cannot open — but
> only when packaging is available, which today it never is. This makes it
> available.

## Goal

Serve a file a client cannot open, by **repackaging it and nothing else**.

Most of this library is MKV holding HEVC or H.264 with AC-3/E-AC-3/AAC — codecs
AVFoundation decodes, in a container it refuses. That is a packaging problem, and a
stream copy solves it: no frame is decoded, no quality is lost, and the
[spike](../apple-client/plan.md#results-of-the-local-pass-2026-08-03) measured 30
seconds of 4K at 26.5 Mbit/s repackaged in 0.11 seconds.

## What the spike already settled

These are measurements, not assumptions, and the design starts from them rather
than re-deriving them:

- **Progressive MP4, served over byte ranges. Not HLS.** Dynamic range has to be
  negotiated in a master playlist, and every master playlist tvOS was offered
  failed to open, while the identical media played when the playlist was skipped. A
  progressive file has no playlist to get wrong. HLS earns its complexity when
  there is a bitrate ladder to switch between; nothing is being re-encoded here, so
  there is none.
- **"Progressive" means non-fragmented, `moov` before the media data.** Fragmented
  MP4 is a different artifact with a different index and nothing has tested it. The
  two must not be used as synonyms.
- **GPAC writes the Dolby Vision configuration; ffmpeg does not.** `MP4Box`
  produces `dvvC` beside `hvcC` and preserves every RPU (756 in, 756 out); an
  ffmpeg stream copy drops the signalling silently, and ffmpeg 8.1.2 exposes no
  option for it.
- **Forcing the `dvh1` sample entry is what engages Dolby Vision.** `hvc1` + `dvvC`
  is the cross-compatible form and a player may read it as HDR10 — AVFoundation
  does. Confirmed on an Apple TV 4K.
- **HEVC must be tagged `hvc1`.** Apple rejects `hev1` outright.
- **The elementary-stream detour is a liability.** Extracting raw `.hevc` and
  re-importing it is how the spike got DV, and it silently defaulted to 25 fps on a
  24 fps source — 1.09 s of drift per 30 s, nearly five minutes across a film.
- **Cover art can masquerade as video.** The 4K sample's MJPEG track is not flagged
  `attached_pic`, so a bare `-map 0:v` packages the cover as a second video
  rendition. The video stream must be mapped explicitly.

## Target behaviour

### Where it runs

In the **`transcode-engine`** app, not in `api`. `api` has deliberately shipped
without ffmpeg since [external track
sidecars](../external-track-sidecars/feature.md), and the engine already has the
tooling, the shared-mount contract, a job API with SSE progress, and cross-app
discovery through `HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL`.

The engine today is a **batch job model**: `POST /jobs` runs to completion and
writes an output file. That is the shape this feature should reuse if it can —
see the production question below — rather than the "live transcoding for streaming
clients" epic its own idea document deferred.

The engine is not publicly exposed, so `api` serves the bytes and the engine only
produces them. Authorization stays where it already is: item id → catalog sandbox →
user access, with the same signed URL tokens the direct path uses.

### The contract it plugs into

None of `/native/v1` changes shape. `native-playback` already returns:

- `remux` with a URL, once `NativePackagingAvailability.IsAvailable` is true;
- the sample entry to write in `signalling` — the one field on that response that
  exists precisely because **we** author the container here.

`GET /native/v1/server` flips `capabilities.packaging` to true with it.

### The production question

A progressive file needs its `moov` before the media data, which is the one real
constraint the approach carries. Two shapes, and the gate below decides:

- **Pre-generated** — the engine's existing job model, output beside the source.
  Simple, reuses everything, seeks instantly, and costs a second copy on disk for
  every title a client cannot direct-play.
- **On demand** — produced per playback. No disk cost, but the whole index must be
  known before the first byte is served, and a seek into a part not yet produced
  has to be answered somehow.

A third shape exists and should be considered rather than assumed away: **remux
once, keep it as a second `MediaSource`**, which is exactly what the transcode
feature already does for a shrunk version. It makes the artifact visible and
deletable in the UI instead of hidden in a cache.

## Deliverables

One PR.

### Phase 1 — the packaging gate

Measurement first, because the answer changes what is built. **No implementation
starts until this phase has an answer**, and its results are written into this
document.

- [ ] **A whole film, not a 30-second slice** — the spike's numbers are from
      slices and prove only that a stream copy is cheap per second.
- [ ] **Time to first frame from cold**, for whichever production shape is being
      measured.
- [ ] **A seek into a part not yet produced**, for the on-demand shape.
- [ ] **60–80 Mbit/s**, the bitrate class the spike never exercised (its 4K sample
      is 26.5 Mbit/s).
- [ ] **Several concurrent clients** against one engine container.
- [ ] **Cancel, restart and cleanup** — what happens to a half-written artifact when
      a client vanishes mid-file.
- [ ] **Multi-audio, sidecar audio, and subtitles** folded into the output.
- [ ] **DV profiles 5 and 8**, and E-AC-3/Atmos passthrough.
- [ ] **Dolby Vision without the elementary-stream detour.** The spike got DV only
      by extracting raw `.hevc` and re-importing it with a hand-set frame rate — the
      very path it then recorded as a liability. A result obtained that way does not
      validate a pipeline that will not use it.
- [ ] **The same tooling inside the Linux `transcode-engine` container**, not on
      macOS. GPAC and ffmpeg behave differently across builds, and the spike ran on
      neither the target OS nor the target image.
- [ ] **Written outcome**: the production shape chosen, with the numbers behind it.

### Phase 2 — packaging in the engine

- [ ] **A packaging operation on the engine** producing a progressive MP4 by stream
      copy: `hvc1` tagging, explicit video mapping so cover art cannot masquerade as
      a rendition, `moov` before the media data, and the requested sample entry.
- [ ] **Dolby Vision signalling** written as asked — cross-compatible or `dvh1` —
      without the elementary-stream detour, or with the frame rate carried
      explicitly if no other route exists.
- [ ] **Track selection** so the output carries what the viewer chose, including
      sidecars folded in as tracks.
- [ ] **Progress and failure** over the existing SSE stream.
- [ ] **Unit tests** for the argument construction, especially the two traps: the
      `hvc1` tag and the explicit video map.

### Phase 3 — serving it

- [ ] **`api` serves the packaged bytes** by byte range under the existing signed
      URL tokens, with the same sandbox and access rules as the direct path.
- [ ] **`NativePackagingAvailability` reflects reality**, so `resolve` starts
      answering `remux` and `GET /native/v1/server` reports `packaging: true`.
- [ ] **Lifecycle** — whatever the gate chose: retention and cleanup for a cache, or
      a visible second `MediaSource` for a kept artifact.
- [ ] **Unit tests**: a remux URL is refused without a valid token, an unpublished
      item is unreachable, and a client that cannot open the container still gets
      `remux` rather than `directPlay`.

### Closing the plan

- [ ] **`feature.md`**, and an update to
      [native-playback](../native-playback/feature.md) where it says packaging does
      not exist yet.
- [ ] **Index** — `node scripts/docs-index.mjs --fix`.
- [ ] **Version bump** — new functionality, so a minor; read `manifest.json` when
      the work lands.

## Open questions

- **Which production shape.** Phase 1 answers it. Pre-generated is the safest and
  the most wasteful; on demand is the opposite; a kept second `MediaSource` sits
  between and reuses machinery that already exists.
- **Does the engine need a session model at all?** Its idea document deferred
  "live transcoding for streaming clients" as a separate epic. If the gate picks a
  produce-then-serve shape, that epic stays deferred and this feature is a new job
  type rather than a new model.
- **What a client is told while a file is being produced.** The `remux` answer is a
  promise that a URL will serve something; whether it may be returned before
  production has started, and what the client shows meanwhile, is only answerable
  once cold-start numbers exist.
- **Whether the stored sample entry should be recorded first.** Nothing captures a
  file's own signalling today, so direct play serves DV blind (see
  [media-probe-providers](../media-probe-providers/plan.md)). It does not block this
  feature — here we author the container — but the two decisions touch the same
  question and may be cheaper together.

## Verification steps

1. `dotnet test` for the API test project.
2. The Phase 1 gate in full, on a Core-managed dev runtime with the engine
   attached, recorded in this document before Phase 2 starts.
3. End to end on an Apple TV 4K: an MKV the client cannot open resolves to `remux`,
   plays, seeks across the whole film, and shows the dynamic range the response
   promised.
4. A source with a sidecar dub plays with that dub selected.
5. Confirm Infuse still plays the same titles through the Jellyfin surface — this
   feature is additive and must not disturb it.
