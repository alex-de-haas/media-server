#!/usr/bin/env python3
"""Walk a Matroska file and produce the sample table an MP4 needs.

Nothing is copied: every sample is recorded as (offset, size) into the source, which
is the whole point of the design — the MP4 will reference these bytes rather than
hold them. Laced audio blocks are split here, because a block is not a sample.
"""
import struct
from dataclasses import dataclass, field

# --- element ids ------------------------------------------------------------------
SEGMENT, INFO, TRACKS, CLUSTER = 0x18538067, 0x1549A966, 0x1654AE6B, 0x1F43B675
TRACK_ENTRY, BLOCK_GROUP, VIDEO, AUDIO, BLOCK_ADD_MAP = 0xAE, 0xA0, 0xE0, 0xE1, 0x41E4
TIMECODE_SCALE, DURATION = 0x2AD7B1, 0x4489
TRACK_NUMBER, TRACK_TYPE, CODEC_ID, CODEC_PRIVATE = 0xD7, 0x83, 0x86, 0x63A2
DEFAULT_DURATION, LANGUAGE, TRACK_NAME = 0x23E383, 0x22B59C, 0x536E
PIXEL_W, PIXEL_H, DISPLAY_W, DISPLAY_H = 0xB0, 0xBA, 0x54B0, 0x54BA
SAMPLING_FREQ, CHANNELS = 0xB5, 0x9F
COLOUR = 0x55B0
MATRIX, RANGE, TRANSFER, PRIMARIES = 0x55B1, 0x55B9, 0x55BA, 0x55BB
BLOCK_ADD_NAME, BLOCK_ADD_EXTRA = 0x41A4, 0x41ED
CLUSTER_TIMECODE, SIMPLE_BLOCK, BLOCK = 0xE7, 0xA3, 0xA1

MASTERS = {SEGMENT, INFO, TRACKS, TRACK_ENTRY, CLUSTER, BLOCK_GROUP, VIDEO, AUDIO,
           BLOCK_ADD_MAP, COLOUR}


@dataclass
class Sample:
    pts: int          # in TimecodeScale ticks
    offset: int       # absolute byte offset of the payload in the source file
    size: int
    key: bool


@dataclass
class Track:
    number: int
    type: int = 0                       # 1 video, 2 audio, 17 subtitle
    codec: str = ""
    codec_private: bytes = b""
    dv_config: bytes = b""              # the dvcC/dvvC payload, straight from the source
    default_duration: int = 0           # nanoseconds
    width: int = 0
    height: int = 0
    display_width: int = 0
    display_height: int = 0
    sample_rate: float = 0.0
    channels: int = 0
    language: str = "und"
    name: str = ""
    samples: list = field(default_factory=list)
    laced_blocks: int = 0
    # Colour is carried, not derived: the source states it and the MP4 repeats it.
    primaries: int = 0
    transfer: int = 0
    matrix: int = 0
    full_range: bool = False


def _vint(f, mask=True):
    b = f.read(1)
    if not b:
        return None, 0
    v = b[0]
    if v == 0:
        return None, 0
    n = 1
    while not (v & (0x80 >> (n - 1))):
        n += 1
        if n > 8:
            return None, 0
    if mask:
        v &= (0x80 >> (n - 1)) - 1
    rest = f.read(n - 1) if n > 1 else b""
    return int.from_bytes(bytes([v]) + rest, "big"), n


def _uint(b):
    return int.from_bytes(b, "big")


def _float(b):
    return struct.unpack(">f" if len(b) == 4 else ">d", b)[0] if b else 0.0


def _delace(payload_off, payload_len, flags, f):
    """Return [(offset, size)] for the frames in one block.

    A laced block holds several frames; their bytes stay contiguous in the source, so
    each frame is still a plain (offset, size) — only the arithmetic differs.
    """
    lacing = (flags >> 1) & 0x03
    if lacing == 0:
        return [(payload_off, payload_len)], False

    here = f.tell()
    f.seek(payload_off)
    count = f.read(1)[0] + 1            # stored as N-1
    consumed = 1
    sizes = []

    if lacing == 2:                     # fixed: equal parts, no size table
        each = (payload_len - consumed) // count
        sizes = [each] * count
    elif lacing == 1:                   # Xiph: 255-terminated sums, last frame implied
        for _ in range(count - 1):
            total = 0
            while True:
                byte = f.read(1)[0]
                consumed += 1
                total += byte
                if byte != 255:
                    break
            sizes.append(total)
        sizes.append(payload_len - consumed - sum(sizes))
    else:                               # EBML: first absolute, rest signed deltas
        first, n = _vint(f)
        consumed += n
        sizes.append(first)
        for _ in range(count - 2):
            raw, n = _vint(f)
            consumed += n
            # Signed vint: bias by half the representable range for its width.
            raw -= (1 << (7 * n - 1)) - 1
            sizes.append(sizes[-1] + raw)
        if count > 1:
            sizes.append(payload_len - consumed - sum(sizes))

    out, pos = [], payload_off + consumed
    for s in sizes:
        out.append((pos, s))
        pos += s
    f.seek(here)
    return out, True


def index(path, progress=None):
    f = open(path, "rb")
    file_size = f.seek(0, 2)
    f.seek(0)

    tracks, timecode_scale, duration = {}, 1_000_000, 0.0
    cluster_time = 0
    cur = None
    add_name = None

    def walk(end, ctx=None):
        nonlocal timecode_scale, duration, cluster_time, cur, add_name
        while f.tell() < end:
            start = f.tell()
            eid, _ = _vint(f, mask=False)
            if eid is None:
                return
            elen, _ = _vint(f)
            if elen is None:
                return
            body = f.tell()

            if eid == TRACK_ENTRY:
                cur = Track(number=0)
                walk(body + elen, "track")
                if cur.number:
                    tracks[cur.number] = cur
                cur = None
            elif eid in MASTERS:
                if eid == CLUSTER and progress:
                    progress(body, file_size)
                walk(body + elen, ctx)
            elif eid in (SIMPLE_BLOCK, BLOCK):
                here = f.tell()
                num, _ = _vint(f)
                rel = struct.unpack(">h", f.read(2))[0]
                flags = f.read(1)[0]
                poff = f.tell()
                plen = elen - (poff - here)
                frames, was_laced = _delace(poff, plen, flags, f)
                t = tracks.get(num)
                if t is not None:
                    if was_laced:
                        t.laced_blocks += 1
                    # Every frame in a laced block shares the block's timestamp here;
                    # the exact per-frame timing is recomputed from the constant frame
                    # duration when the MP4 sample table is built.
                    for off, size in frames:
                        t.samples.append(Sample(cluster_time + rel, off, size,
                                                bool(flags & 0x80) if eid == SIMPLE_BLOCK else True))
                f.seek(body + elen)
            else:
                data = f.read(elen) if elen <= 1 << 20 else b""
                if eid == TIMECODE_SCALE:
                    timecode_scale = _uint(data)
                elif eid == DURATION:
                    duration = _float(data)
                elif eid == CLUSTER_TIMECODE:
                    cluster_time = _uint(data)
                elif cur is not None:
                    if eid == TRACK_NUMBER:
                        cur.number = _uint(data)
                    elif eid == TRACK_TYPE:
                        cur.type = _uint(data)
                    elif eid == CODEC_ID:
                        cur.codec = data.rstrip(b"\x00").decode("latin1")
                    elif eid == CODEC_PRIVATE:
                        cur.codec_private = data
                    elif eid == DEFAULT_DURATION:
                        cur.default_duration = _uint(data)
                    elif eid == LANGUAGE:
                        cur.language = data.rstrip(b"\x00").decode("latin1")
                    elif eid == TRACK_NAME:
                        cur.name = data.rstrip(b"\x00").decode("utf-8", "replace")
                    elif eid == PIXEL_W:
                        cur.width = _uint(data)
                    elif eid == PIXEL_H:
                        cur.height = _uint(data)
                    elif eid == DISPLAY_W:
                        cur.display_width = _uint(data)
                    elif eid == DISPLAY_H:
                        cur.display_height = _uint(data)
                    elif eid == SAMPLING_FREQ:
                        cur.sample_rate = _float(data)
                    elif eid == CHANNELS:
                        cur.channels = _uint(data)
                    elif eid == PRIMARIES:
                        cur.primaries = _uint(data)
                    elif eid == TRANSFER:
                        cur.transfer = _uint(data)
                    elif eid == MATRIX:
                        cur.matrix = _uint(data)
                    elif eid == RANGE:
                        cur.full_range = _uint(data) == 2
                    elif eid == BLOCK_ADD_NAME:
                        add_name = data.rstrip(b"\x00").decode("latin1")
                    elif eid == BLOCK_ADD_EXTRA:
                        if add_name and "Dolby Vision" in add_name:
                            cur.dv_config = data
                        add_name = None
                f.seek(body + elen)

            if f.tell() <= start:
                return

    walk(file_size)
    f.close()
    return {
        "path": path,
        "file_size": file_size,
        "timecode_scale": timecode_scale,
        "duration_ticks": duration,
        "tracks": tracks,
    }


if __name__ == "__main__":
    import sys
    ix = index(sys.argv[1])
    print(f"file {ix['file_size']:,} B   TimecodeScale {ix['timecode_scale']}   "
          f"duration {ix['duration_ticks'] * ix['timecode_scale'] / 1e9:.2f}s")
    for n, t in sorted(ix["tracks"].items()):
        kind = {1: "video", 2: "audio", 17: "subs"}.get(t.type, str(t.type))
        extra = ""
        if t.type == 1:
            extra = f" {t.width}x{t.height} dur={t.default_duration}ns col={t.primaries}/{t.transfer}/{t.matrix}"
            if t.dv_config:
                extra += f" DV[{t.dv_config[:5].hex()}]"
        elif t.type == 2:
            extra = f" {t.sample_rate:.0f}Hz {t.channels}ch"
        print(f"  {n:>2} {kind:<6} {t.codec:<20} samples={len(t.samples):<7} "
              f"laced_blocks={t.laced_blocks:<6} private={len(t.codec_private)}B{extra}")
