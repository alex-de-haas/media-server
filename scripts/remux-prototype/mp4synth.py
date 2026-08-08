#!/usr/bin/env python3
"""Compute an MP4 header whose samples live in an untouched Matroska file.

The trick that makes this cheap: an `mdat` is an opaque blob, so it can wrap the
entire source verbatim and the sample table can point at payload positions inside it.
Output offset is then just header size + source offset, and nothing is repackaged.

    [ftyp][moov][mdat header][ ...the whole .mkv, byte for byte... ]

The Matroska framing bytes inside `mdat` are never referenced by any sample, and a
player never reads them.
"""
import struct

NS = 1_000_000_000          # every track keeps time in nanoseconds


def box(kind, *parts):
    payload = b"".join(parts)
    return struct.pack(">I", 8 + len(payload)) + kind + payload


def full(kind, version, flags, *parts):
    return box(kind, struct.pack(">B", version) + struct.pack(">I", flags)[1:], *parts)


# ------------------------------------------------------------------- AC-3 specifics

class _Bits:
    def __init__(self, data):
        self.d, self.p = data, 0

    def read(self, n):
        v = 0
        for _ in range(n):
            byte = self.d[self.p >> 3]
            v = (v << 1) | ((byte >> (7 - (self.p & 7))) & 1)
            self.p += 1
        return v


AC3_RATES = [48000, 44100, 32000]
AC3_CHANNELS = [2, 1, 2, 3, 3, 4, 4, 5]


def parse_ac3(frame):
    """Enough of a syncframe to build `dac3` — the box an MP4 AC-3 track needs."""
    if frame[:2] != b"\x0b\x77":
        return None
    b = _Bits(frame)
    b.read(16); b.read(16)                       # syncword, crc1
    fscod = b.read(2)
    frmsizecod = b.read(6)
    bsid = b.read(5)
    bsmod = b.read(3)
    acmod = b.read(3)
    if (acmod & 1) and acmod != 1:
        b.read(2)                                # cmixlev
    if acmod & 4:
        b.read(2)                                # surmixlev
    if acmod == 2:
        b.read(2)                                # dsurmod
    lfeon = b.read(1)
    bit_rate_code = frmsizecod >> 1

    packed = (fscod << 22) | (bsid << 17) | (bsmod << 14) | (acmod << 11) \
        | (lfeon << 10) | (bit_rate_code << 5)
    return {
        "dac3": struct.pack(">I", packed)[1:],   # three bytes
        "rate": AC3_RATES[fscod] if fscod < 3 else 48000,
        "channels": AC3_CHANNELS[acmod] + lfeon,
    }


# ---------------------------------------------------------------------- sample table

def _stts(deltas):
    runs = []
    for d in deltas:
        if runs and runs[-1][1] == d:
            runs[-1][0] += 1
        else:
            runs.append([1, d])
    return full(b"stts", 0, 0, struct.pack(">I", len(runs)),
                b"".join(struct.pack(">II", c, d) for c, d in runs))


def _ctts(offsets):
    runs = []
    for o in offsets:
        if runs and runs[-1][1] == o:
            runs[-1][0] += 1
        else:
            runs.append([1, o])
    # Version 1 so a negative composition offset is legal.
    return full(b"ctts", 1, 0, struct.pack(">I", len(runs)),
                b"".join(struct.pack(">Ii", c, o) for c, o in runs))


def _stbl(entry, samples, deltas, cts, sync):
    parts = [box(b"stsd", struct.pack(">B", 0) + b"\x00\x00\x00" +
                 struct.pack(">I", 1) + entry),
             _stts(deltas)]
    if cts is not None:
        parts.append(_ctts(cts))
    if sync is not None:
        parts.append(full(b"stss", 0, 0, struct.pack(">I", len(sync)),
                          b"".join(struct.pack(">I", i) for i in sync)))
    # One sample per chunk keeps the mapping trivial: co64 is simply the offsets.
    parts.append(full(b"stsc", 0, 0, struct.pack(">I", 1), struct.pack(">III", 1, 1, 1)))
    parts.append(full(b"stsz", 0, 0, struct.pack(">I", 0), struct.pack(">I", len(samples)),
                      b"".join(struct.pack(">I", s.size) for s in samples)))
    parts.append(full(b"co64", 0, 0, struct.pack(">I", len(samples)),
                      b"".join(struct.pack(">Q", s.offset) for s in samples)))
    return box(b"stbl", *parts)


# ----------------------------------------------------------------------------- entries

# Matroska CodecID -> (configuration box, default sample entry). The configuration
# record is carried verbatim in both cases: Matroska stores exactly the bytes the MP4
# box wants.
VIDEO_CODECS = {
    "V_MPEGH/ISO/HEVC": (b"hvcC", "hvc1"),
    "V_MPEG4/ISO/AVC": (b"avcC", "avc1"),
}


def video_entry(track, sample_entry):
    config_box, default_entry = VIDEO_CODECS.get(track.codec, (b"hvcC", "hvc1"))
    # A Dolby Vision entry only makes sense over HEVC; anything else keeps its own.
    if config_box != b"hvcC":
        sample_entry = default_entry
    extras = [box(config_box, track.codec_private)]
    if track.transfer or track.primaries:
        # AVFoundation reads the container's colr; without it the format description
        # reports no transfer function at all, which is how HDR gets lost.
        extras.append(box(b"colr", b"nclx"
                          + struct.pack(">HHH", track.primaries, track.transfer, track.matrix)
                          + struct.pack(">B", 0x80 if track.full_range else 0x00)))
    if track.dv_config:
        # Straight from the source's BlockAdditionMapping — the configuration is not
        # derived, it is carried.
        extras.append(box(b"dvvC", track.dv_config))
    body = (b"\x00" * 6 + struct.pack(">H", 1)
            + b"\x00" * 16
            + struct.pack(">HH", track.width, track.height)
            + struct.pack(">II", 0x00480000, 0x00480000)
            + b"\x00" * 4
            + struct.pack(">H", 1)
            + b"\x00" * 32
            + struct.pack(">H", 0x0018)
            + b"\xff\xff")
    return box(sample_entry.encode("ascii"), body, *extras)


def audio_entry(track, ac3):
    body = (b"\x00" * 6 + struct.pack(">H", 1)
            + b"\x00" * 8
            + struct.pack(">HH", ac3["channels"], 16)
            + b"\x00" * 4
            + struct.pack(">I", ac3["rate"] << 16))
    return box(b"ac-3", body, box(b"dac3", ac3["dac3"]))


# ------------------------------------------------------------------------------ build

def build(ix, want_tracks, sample_entry="dvh1", mdat_header=16):
    """Return (header_bytes, total_size, report).

    Offsets depend on the header's own length, so the header is built twice: once to
    learn its size, once with the real offsets. The second build is the same length
    because every offset field is fixed width.
    """
    src = open(ix["path"], "rb")
    scale = ix["timecode_scale"]

    prepared = []
    for num in want_tracks:
        t = ix["tracks"][num]
        if t.type == 1:
            n = len(t.samples)
            pts = [s.pts * scale for s in t.samples]
            # The decode timeline is the presentation timestamps in sorted order. A
            # uniform duration taken from DefaultDuration drifts — on a 2 h source it
            # parted company with the real timestamps by half a minute — so the
            # durations are read from the file rather than assumed.
            dts = sorted(pts)
            deltas = [dts[i + 1] - dts[i] for i in range(n - 1)]
            deltas.append(t.default_duration or (deltas[-1] if deltas else 0))
            cts = [p - d for p, d in zip(pts, dts)]
            # No reordering means no composition offsets are needed at all.
            if all(c == 0 for c in cts):
                cts = None
            sync = [i + 1 for i, s in enumerate(t.samples) if s.key]
            if len(sync) == n:
                sync = None
            prepared.append({"t": t, "kind": "video", "deltas": deltas, "cts": cts,
                             "sync": sync, "timescale": NS,
                             "duration": sum(deltas), "ac3": None})
        elif t.type == 2:
            src.seek(t.samples[0].offset)
            ac3 = parse_ac3(src.read(16))
            if ac3 is None:
                continue
            # AC-3 is always 1536 samples per frame, so the timing is exact and the
            # per-frame timestamps a laced block cannot give are not needed.
            dur = 1536 * NS // ac3["rate"]
            n = len(t.samples)
            prepared.append({"t": t, "kind": "audio", "deltas": [dur] * n, "cts": None,
                             "sync": None, "timescale": NS,
                             "duration": dur * n, "ac3": ac3})
    src.close()

    movie_duration = max(p["duration"] for p in prepared)

    def assemble(base):
        traks = []
        for i, p in enumerate(prepared, start=1):
            t = p["t"]
            samples = [type(s)(s.pts, s.offset + base, s.size, s.key) for s in t.samples]
            entry = (video_entry(t, sample_entry) if p["kind"] == "video"
                     else audio_entry(t, p["ac3"]))
            stbl = _stbl(entry, samples, p["deltas"], p["cts"], p["sync"])
            handler = b"vide" if p["kind"] == "video" else b"soun"
            media_header = (box(b"vmhd", b"\x00\x00\x00\x01" + b"\x00" * 8)
                            if p["kind"] == "video"
                            else box(b"smhd", b"\x00" * 8))
            tkhd = full(b"tkhd", 1, 3,
                        struct.pack(">QQ", 0, 0) + struct.pack(">I", i) + b"\x00" * 4
                        + struct.pack(">Q", movie_duration * 1000 // NS)
                        + b"\x00" * 8 + struct.pack(">hhH", 0, 0, 0) + b"\x00" * 2
                        + struct.pack(">9i", 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000)
                        + struct.pack(">II",
                                      (t.display_width or t.width) << 16 if p["kind"] == "video" else 0,
                                      (t.display_height or t.height) << 16 if p["kind"] == "video" else 0))
            mdhd = full(b"mdhd", 1, 0,
                        struct.pack(">QQ", 0, 0) + struct.pack(">I", p["timescale"])
                        + struct.pack(">Q", p["duration"])
                        + struct.pack(">HH", 0x55C4, 0))       # 'und'
            hdlr = full(b"hdlr", 0, 0, b"\x00" * 4 + handler + b"\x00" * 12 + b"prototype\x00")
            dinf = box(b"dinf", full(b"dref", 0, 0, struct.pack(">I", 1),
                                     full(b"url ", 0, 1)))
            minf = box(b"minf", media_header, dinf, stbl)
            mdia = box(b"mdia", mdhd, hdlr, minf)
            traks.append(box(b"trak", tkhd, mdia))

        mvhd = full(b"mvhd", 1, 0,
                    struct.pack(">QQ", 0, 0) + struct.pack(">I", 1000)
                    + struct.pack(">Q", movie_duration * 1000 // NS)
                    + struct.pack(">I", 0x00010000) + struct.pack(">H", 0x0100)
                    + b"\x00" * 10
                    + struct.pack(">9i", 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000)
                    + b"\x00" * 24 + struct.pack(">I", len(prepared) + 1))
        return box(b"moov", mvhd, *traks)

    ftyp = box(b"ftyp", b"isom" + struct.pack(">I", 0x200) + b"isomiso2mp41hvc1dby1")
    provisional = assemble(0)
    base = len(ftyp) + len(provisional) + mdat_header
    moov = assemble(base)
    assert len(moov) == len(provisional), "header size moved between passes"

    mdat = struct.pack(">I", 1) + b"mdat" + struct.pack(">Q", mdat_header + ix["file_size"])
    header = ftyp + moov + mdat
    assert len(header) == base

    report = {
        "header_bytes": len(header),
        "total_size": len(header) + ix["file_size"],
        "tracks": [(p["t"].number, p["kind"], len(p["t"].samples)) for p in prepared],
        "sample_entry": next(
            (VIDEO_CODECS.get(p["t"].codec, (b"hvcC", sample_entry))[1]
             if VIDEO_CODECS.get(p["t"].codec, (b"hvcC", None))[0] != b"hvcC" else sample_entry)
            for p in prepared if p["kind"] == "video"),
        "dv": bool(any(p["kind"] == "video" and p["t"].dv_config for p in prepared)),
    }
    return header, len(header) + ix["file_size"], report


if __name__ == "__main__":
    import sys
    import mkvindex
    ix = mkvindex.index(sys.argv[1])
    want = [n for n, t in sorted(ix["tracks"].items()) if t.type in (1, 2)][:2]
    header, total, rep = build(ix, want)
    open(sys.argv[2], "wb").write(header)
    print(f"header {rep['header_bytes']:,} B   total {total:,} B   "
          f"entry {rep['sample_entry']}   DV={rep['dv']}")
    print(f"tracks {rep['tracks']}")
