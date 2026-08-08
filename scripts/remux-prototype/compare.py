#!/usr/bin/env python3
"""Step 1 of the remux-streaming prototype gate.

The design assumes a Matroska block payload IS the MP4 sample, byte for byte, so an
index can reference samples in the source instead of copying them. Video should hold
(HEVC is length-prefixed in both). Audio is the risk: Matroska may LACE several AC-3
frames into one block, which breaks one-block-one-sample.

This reads both containers directly — no ffmpeg — and compares the actual bytes.
"""
import hashlib
import struct
import sys
from collections import defaultdict

# ---------------------------------------------------------------------------- EBML

MASTER = {
    0x18538067,  # Segment
    0x1F43B675,  # Cluster
    0x1654AE6B,  # Tracks
    0xAE,        # TrackEntry
    0xA0,        # BlockGroup
}
LACING = {0: "none", 1: "Xiph", 2: "fixed", 3: "EBML"}


def read_vint(f, mask_length=True):
    """EBML variable-length integer. Returns (value, byte_length)."""
    first = f.read(1)
    if not first:
        return None, 0
    b = first[0]
    if b == 0:
        return None, 0
    length = 1
    while not (b & (0x80 >> (length - 1))):
        length += 1
        if length > 8:
            return None, 0
    value = b
    if mask_length:
        value = b & ((0x80 >> (length - 1)) - 1)
    value = int.from_bytes(bytes([value]) + f.read(length - 1), "big") if length > 1 else value
    return value, length


def read_id(f):
    """Element IDs keep their length marker — that is what makes them unique."""
    return read_vint(f, mask_length=False)


def parse_mkv(path):
    """Walk clusters and yield every block: (track, offset, size, keyframe, lacing)."""
    blocks = []
    codecs = {}
    f = open(path, "rb")
    size = f.seek(0, 2)
    f.seek(0)

    def walk(end, in_tracks=False):
        cur_track = None
        while f.tell() < end:
            start = f.tell()
            eid, idlen = read_id(f)
            if eid is None:
                return
            elen, _ = read_vint(f)
            if elen is None:
                return
            body = f.tell()

            if eid == 0xAE:                      # TrackEntry
                walk(body + elen, in_tracks=True)
            elif eid in MASTER:
                walk(body + elen, in_tracks)
            elif in_tracks and eid == 0xD7:      # TrackNumber
                cur_track = int.from_bytes(f.read(elen), "big")
                codecs.setdefault(cur_track, "?")
            elif in_tracks and eid == 0x86:      # CodecID
                cid = f.read(elen).rstrip(b"\x00").decode("latin1")
                if cur_track is not None:
                    codecs[cur_track] = cid
            elif eid in (0xA3, 0xA1):            # SimpleBlock / Block
                here = f.tell()
                track, tlen = read_vint(f)
                f.read(2)                        # relative timecode
                flags = f.read(1)[0]
                payload_off = f.tell()
                payload_len = elen - (payload_off - here)
                blocks.append({
                    "track": track,
                    "offset": payload_off,
                    "size": payload_len,
                    "key": bool(flags & 0x80) if eid == 0xA3 else True,
                    "lacing": LACING[(flags & 0x06) >> 1],
                })
                f.seek(body + elen)
            else:
                f.seek(body + elen)
            if f.tell() <= start:
                return

    walk(size)
    f.close()
    return blocks, codecs


# ----------------------------------------------------------------------------- MP4

def parse_mp4(path):
    """Return per-track sample lists from the sample tables: (offset, size)."""
    data = open(path, "rb").read()
    tracks = []

    def boxes(start, end):
        i = start
        while i + 8 <= end:
            size = struct.unpack(">I", data[i:i + 4])[0]
            typ = data[i + 4:i + 8]
            hdr = 8
            if size == 1:
                size = struct.unpack(">Q", data[i + 8:i + 16])[0]
                hdr = 16
            elif size == 0:
                size = end - i
            yield typ, i + hdr, i + size
            i += size

    def find(start, end, path_types):
        for typ, bs, be in boxes(start, end):
            if typ == path_types[0]:
                if len(path_types) == 1:
                    yield bs, be
                else:
                    yield from find(bs, be, path_types[1:])

    for moov_s, moov_e in find(0, len(data), [b"moov"]):
        for trak_s, trak_e in find(moov_s, moov_e, [b"trak"]):
            stbl = list(find(trak_s, trak_e, [b"mdia", b"minf", b"stbl"]))
            if not stbl:
                continue
            s, e = stbl[0]
            entry = None
            sizes, chunk_offsets, stsc = [], [], []
            for typ, bs, be in boxes(s, e):
                if typ == b"stsd":
                    entry = data[bs + 12:bs + 16].decode("latin1", "replace")
                elif typ == b"stsz":
                    uniform = struct.unpack(">I", data[bs + 4:bs + 8])[0]
                    count = struct.unpack(">I", data[bs + 8:bs + 12])[0]
                    sizes = ([uniform] * count if uniform else
                             list(struct.unpack(f">{count}I", data[bs + 12:bs + 12 + 4 * count])))
                elif typ in (b"stco", b"co64"):
                    count = struct.unpack(">I", data[bs + 4:bs + 8])[0]
                    fmt, width = (">I", 4) if typ == b"stco" else (">Q", 8)
                    chunk_offsets = [struct.unpack(fmt, data[bs + 8 + i * width:bs + 8 + (i + 1) * width])[0]
                                     for i in range(count)]
                elif typ == b"stsc":
                    count = struct.unpack(">I", data[bs + 4:bs + 8])[0]
                    stsc = [struct.unpack(">III", data[bs + 8 + i * 12:bs + 8 + (i + 1) * 12])
                            for i in range(count)]
            if not sizes:
                continue

            # Expand stsc into a per-chunk sample count, then lay samples out.
            per_chunk = []
            for i, (first, spc, _) in enumerate(stsc):
                last = stsc[i + 1][0] - 1 if i + 1 < len(stsc) else len(chunk_offsets)
                per_chunk += [spc] * (last - first + 1)
            samples, si = [], 0
            for ci, off in enumerate(chunk_offsets):
                n = per_chunk[ci] if ci < len(per_chunk) else 0
                pos = off
                for _ in range(n):
                    if si >= len(sizes):
                        break
                    samples.append((pos, sizes[si]))
                    pos += sizes[si]
                    si += 1
            tracks.append({"entry": entry, "samples": samples})
    return tracks


# ------------------------------------------------------------------------ compare

def main():
    mkv_path, mp4_path = sys.argv[1], sys.argv[2]
    blocks, codecs = parse_mkv(mkv_path)
    tracks = parse_mp4(mp4_path)

    by_track = defaultdict(list)
    for b in blocks:
        by_track[b["track"]].append(b)

    print("=== Matroska ===")
    for t, bs in sorted(by_track.items()):
        lacings = {b["lacing"] for b in bs}
        print(f"  track {t}  codec={codecs.get(t,'?'):<12} blocks={len(bs):<6} "
              f"lacing={'/'.join(sorted(lacings))}  bytes={sum(b['size'] for b in bs):,}")

    print("=== MP4 ===")
    for i, tr in enumerate(tracks):
        print(f"  track {i}  entry={tr['entry']:<6} samples={len(tr['samples']):<6} "
              f"bytes={sum(s[1] for s in tr['samples']):,}")

    mkvf = open(mkv_path, "rb")
    mp4f = open(mp4_path, "rb")

    print("=== byte comparison ===")
    for t, bs in sorted(by_track.items()):
        # Match by sample count; a laced track will not line up and that is the finding.
        cand = [tr for tr in tracks if len(tr["samples"]) == len(bs)]
        if not cand:
            counts = [len(tr["samples"]) for tr in tracks]
            print(f"  track {t}: NO MP4 TRACK WITH {len(bs)} SAMPLES (mp4 has {counts}) "
                  f"-> block != sample")
            continue
        tr = cand[0]
        n = min(len(bs), 400)
        step = max(1, len(bs) // n)
        checked = mismatched = 0
        first_bad = None
        for i in range(0, len(bs), step):
            mkvf.seek(bs[i]["offset"]); a = mkvf.read(bs[i]["size"])
            off, size = tr["samples"][i]
            mp4f.seek(off); b = mp4f.read(size)
            checked += 1
            if a != b:
                mismatched += 1
                if first_bad is None:
                    first_bad = (i, len(a), len(b),
                                 hashlib.sha1(a).hexdigest()[:12],
                                 hashlib.sha1(b).hexdigest()[:12])
        verdict = "IDENTICAL" if mismatched == 0 else f"{mismatched}/{checked} DIFFER"
        print(f"  track {t} ({codecs.get(t,'?')}) vs mp4 entry {tr['entry']}: "
              f"{checked} samples checked -> {verdict}")
        if first_bad:
            i, la, lb, ha, hb = first_bad
            print(f"      first mismatch at sample {i}: mkv {la} B {ha} / mp4 {lb} B {hb}")


main()
