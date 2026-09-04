# Dolby Vision Profile

Created: 2026-09-04
Updated: 2026-09-04

The library tells apart the Dolby Vision a client will play from the Dolby Vision it will
quietly show as HDR10, and offers a lossless way to turn the second kind into the first.

Spans [media-probe-providers](../media-probe-providers/feature.md) (storage),
[convert-dialog](../convert-dialog/feature.md), [native-playback](../native-playback/feature.md),
[remux-streaming](../remux-streaming/feature.md),
[jellyfin-compatibility](../jellyfin-compatibility/feature.md), [apple-client](../apple-client/feature.md),
and the transcode engine's
[dolby-vision-conversion](https://github.com/alex-de-haas/transcode-engine/blob/main/docs/features/dolby-vision-conversion/feature.md).
Each of those documents describes its own half; this one is the thread through them.

## The distinction

`Dolby Vision` as a label says whether a stream carries a configuration record, and nothing
about what is in it. Apple TV and Infuse decode single-layer Dolby Vision only — profile 5,
and profile 8 over an HDR10 or HLG base layer. A **profile 7** source, which every UHD
Blu-ray remux is, keeps its RPU in an enhancement layer no Apple device decodes, and plays
as its HDR10 base layer in the native client and in Infuse alike. This library held both
under one word: *Starship Troopers (1997)*, profile 7 with compatibility id 6, played
HDR10; *Avatar (2009)*, profile 8.1, played Dolby Vision.

The record that settles it is 24 bytes in the container header — an MP4 `dvcC`/`dvvC` box,
a Matroska `BlockAdditionMapping` — and both probe providers read it. Per video stream the
library stores `DvProfile`, `DvLevel`, `DvBlSignalCompatibilityId` and `DvElPresent`;
`HdrFormat` keeps its vocabulary and the detail sits beside it. A row labelled Dolby Vision
before the record was stored is filled in by the catalog **refresh** pass, the one place
that pass reaches past provenance. See
[media probe providers](../media-probe-providers/feature.md#hdr-says-how-sure-it-is).

## What a viewer sees

Every stream DTO carries `dolbyVision: { profile, level, blCompatibilityId, enhancementLayer }`
or null. The web version card shows the dynamic range as badges — `HDR10`, `HDR10+`, `HLG`,
`Dolby Vision 8.1`, `Dolby Vision 7` — one per format the probe named, so a `Dolby Vision ·
HDR10` value yields two; profile 8 is named by its base layer, the others by profile alone,
and the bare `Dolby Vision` stays while a profile is not yet recorded. A dual layer carries
the note *Apple TV and Infuse play its HDR10 base layer*. Text marks, not logos: the Dolby
Vision mark is a licensed trademark, and a capsule reads the same. The tvOS title screen
shows the same capsules over a version's tracks, with *Plays as HDR10 on this device* for a
dual layer — the client knows what the device does where the server does not
([apple client](../apple-client/feature.md#what-the-title-screen-says-about-the-picture)).

The Jellyfin surface reports Jellyfin's own fields — `DvProfile`, `DvLevel`,
`DvBlSignalCompatibilityId`, the three layer flags, `VideoDoViTitle` — and a `VideoRangeType`
that follows the profile (`DOVI`, `DOVIWithHDR10`, `DOVIWithHLG`, `DOVIWithSDR`), collapsing
to `HDR10` where the profile is not yet recorded, as every Dolby Vision stream did before.

## The remux path stops promising what it cannot deliver

Signalling on the remux path follows the record: `dvh1` is asked for only when the client
reported Dolby Vision *and* the source is a form a single-layer decoder plays. A profile 7
source is written as plain `hvc1` with **no** Dolby Vision box — its RPU lives in
`BlockAdditions` the index never carries, so a record would describe metadata the output
does not contain. **The viewer still sees HDR10, exactly as before**; what changes is that
the server stops claiming `dvh1` for a stream it wrote without Dolby Vision. The box a
single-layer record goes in is named by its own profile byte, `dvcC` up to 7 and `dvvC`
from 8, so nothing has to be carried from the source mapping. A source whose profile is not
yet recorded keeps the label-based answer, so nothing regresses before the refresh pass has
run. Dolby Vision for a profile 7 title comes only from the conversion.

## Conversion

The convert dialog offers, on a profile 7 source with the video kept, **Convert Dolby
Vision to profile 8.1 (single layer)**: the HEVC picture is copied byte for byte, the RPU
metadata is rewritten to profile 8.1, and the enhancement layer — 1.6 % of such a file — is
dropped. The result plays as Dolby Vision on Apple TV, in the native client and in Infuse.
It is a video copy only, shown only when the engine advertises the tools
(`GET /api/transcode/availability` → `dolbyVisionConversion`), refused by the service with
a re-encode and on a version whose picture is not profile 7, and named `DV 8.1` in the
version label so it never lands on a plain remux's path. The engine runs it as four tool
stages; the details are its own
([dolby-vision-conversion](https://github.com/alex-de-haas/transcode-engine/blob/main/docs/features/dolby-vision-conversion/feature.md)).
The result is a new version beside the original, as every conversion is; deleting the
original stays the operator's step. Converting on ingest is not done.

## Testing Expectations

Backend tests use xUnit and Imposter. Required coverage lives with each half:

- `DolbyVisionConfigurationTests` — the record parsed from real files (profile 7 with an
  enhancement layer and compatibility id 6, 8.1, 8.4, 5), the fields that straddle bytes,
  fewer than five bytes being no record, and which records a single-layer decoder plays.
- `HeaderMediaProbeTests` — the record read out of an MP4 sample entry and out of a
  Matroska `BlockAdditionMapping`, a name-only mapping still counting, a mapping of another
  kind not, and a stream without one carrying no detail.
- `RemoteMediaProbeTests` — the engine's `dolbyVision` object mapped, and its absence.
- `LibraryMaintenanceServiceTests` — the refresh pass filling the record in for a labelled
  engine row without one, and leaving an engine row alone that has nothing to gain.
- `DynamicRangeTests` — signalling by profile: 5, 8.1 and 8.4 as `dvh1`; 7 and 8.2 as
  `hvc1`; an unrecorded profile keeping the label-based answer; a client without Dolby
  Vision unmoved by any of it.
- `Mp4SynthesizerTests` — a profile 7 record written as plain `hvc1` with no Dolby Vision
  box even when asked for; the box named by the record's profile.
- `JellyfinMappingTests` — `VideoRangeType` and `VideoDoViTitle` per profile.
- `TranscodeServiceTests` — `ResolveDolbyVision`: keep and absent as the default, the
  engine's spelling accepted, an unknown word, a re-encode, every other profile by name, an
  unrecorded profile told from no Dolby Vision, and the picture judged rather than the cover
  art; `VersionLabel` carrying `DV 8.1` after the audio and before `Merged`.
- `RemoteTranscodeEngineWireTests` — the mode travelling under the engine's name, null when
  kept, and the tooling read from the engine's hardware report including an engine from
  before the field.
- `DolbyVisionProjectionTests` — the four columns read as one object, or none.
- Vitest — the badge labels per format and profile, the dual-layer note, and the re-encode
  warning per profile (`format.test.ts`).
- MediaKit `DynamicRangeTests` — the same labels, badges and note, and a track decoding the
  record and its absence.

Not verified here: the end-to-end run on a real profile 7 source — *Starship Troopers*
converted, the output probed as profile 8 with compatibility id 1, and playing as Dolby
Vision on an Apple TV 4K in both clients. No test fixture can be a UHD Blu-ray; it is the
first check after deploy, against the production library.
