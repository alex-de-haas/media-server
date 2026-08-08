# Remux prototype

A throwaway spike that answers the Phase 0 gate of
[remux-streaming](../../docs/features/remux-streaming/plan.md): can an MP4 be
**computed** from an index over an untouched Matroska file, and will an Apple TV play
it in Dolby Vision?

It can, and it does. This is kept because the code is the shortest statement of what
the design does, and because three of its lessons were learned by failing into them.

**This is not production code.** It is Python, it has no tests, it handles one shape
of source, and it exists to be read and re-run — not to be shipped. The real
implementation is `api`'s to write.

## What it does

```
[ftyp][moov][mdat header][ ...the whole .mkv, byte for byte... ]
```

An `mdat` is an opaque blob, so it can wrap the entire source and the sample table
can point at payload positions inside it. Output offset is then just header size plus
source offset: answering a byte range becomes reading the same range from the `.mkv`.
Nothing is repackaged, nothing is stored, and the Matroska framing bytes inside
`mdat` are never referenced by any sample.

## Files

| | |
| --- | --- |
| `mkvindex.py` | Walks a Matroska file into a sample table — per frame the track, timestamp, size, offset and keyframe flag. De-laces audio blocks, because a block is not a sample. |
| `mp4synth.py` | Turns that table into an MP4 header: `hvcC` from the track's `CodecPrivate`, the Dolby Vision configuration from its `BlockAdditionMapping`, `colr` from the `Colour` element, `dvh1` or `hvc1` as asked. |
| `serve.py` | An HTTP range server that synthesises per request: header from memory, media straight out of the source. |
| `compare.py` | The byte-identity check — are a Matroska block payload and an MP4 sample the same bytes? |
| `lacingcheck.py` | Surveys a library for lacing, which is invisible in ffmpeg-produced test material. |
| `avcheck.swift` | Loads a URL through macOS AVFoundation and reports what it resolved. |
| `tvos-harness/` | A tvOS app that plays one URL from a cold launch, probes seeking, and holds the picture so the television's info panel can be read. |

## Running it

```bash
cd scripts/remux-prototype
python3 mkvindex.py /path/to/film.mkv                 # what the source holds
python3 serve.py /path/to/film.mkv 8975               # serve it as an MP4
swiftc -O avcheck.swift -o avcheck && ./avcheck http://127.0.0.1:8975/movie.mp4
```

`serve.py` binds `0.0.0.0`, so the same URL works from an Apple TV on the LAN. The
tvOS harness carries its own instructions; it takes the signing team as
`TVSPIKE_TEAM=<your-team-id>` on the `xcodebuild` command line and the URL to play
from `SPIKE_BASE` / `SPIKE_PATH` at launch, so neither is edited into the source.

Nothing here writes to disk.

## What it measured

Against a 2 h 12 m, 26.4 GB HEVC Dolby Vision profile 8.1 source, on an Apple TV 4K
(tvOS 26.5):

| | |
| --- | --- |
| Index walk | 27 s cold, 2.3 s warm, on an SSD |
| Synthesised header | 7.8 MB — 0.0296 % of the source |
| Written to disk | nothing |
| Time to `readyToPlay` | 0.43 s |
| Reported duration | 7951.10 s, exactly the source's |
| Seeks (1:00:00 / 10:00 / 2:11:40) | 0.35 s, 0.35 s, 2.51 s |
| Stalls | 0 |
| Dynamic range on the television | **Dolby Vision** |

Requests needed before playback starts: **7**, against **3309** for a fragmented MP4
of the same film. A real sample table is read once; fragments have to be walked.

## Three traps, each found by falling into it

- **A uniform sample duration drifts.** Taking `DefaultDuration` as the frame
  duration parted company with the real timestamps by 33 s over this film, and was
  invisible on a 30-second test slice. Durations must come from the timestamps; the
  decode timeline is the presentation timestamps in sorted order.
- **Truncating an *explicit* byte range reads as a failed request.** A client that
  asks for `bytes=0-67832060` and receives eight megabytes gives up. Only an
  open-ended range may be answered with a window.
- **An open-ended range must be streamed, not assembled.** The first version built
  the response in memory, which for a 26 GB source is exactly as bad as it sounds.

And two more from the surrounding measurements, both in
[the plan](../../docs/features/remux-streaming/plan.md): AVFoundation refuses a
server that does not support `Range` at all, and refuses one that will not declare
the total length.

One more worth knowing before trusting a test file: **the library's originals carry
no container-level `Colour` element** — the colour information lives in the HEVC SPS,
and `ffprobe` reports it from there. A slice remuxed by ffmpeg *does* get one, which
is how the difference surfaced. The Dolby Vision run above therefore wrote no `colr`
box at all and still engaged DV on the television, because the decoder reads the VUI.
A production implementation should still read the SPS when the container is silent,
so the format description is complete for content that carries no DV configuration
to fall back on.

## What it does not do

Subtitles, sidecar tracks, track selection, more than one audio track, anything but
HEVC/H.264 with AC-3, and any container other than Matroska. Those are the real
implementation's problems, and the plan lists them.
