#!/usr/bin/env python3
"""Report which tracks in a library's Matroska files use lacing.

Lacing is why an index cannot assume one block is one sample. It is invisible in test
material produced by ffmpeg, which never laces — so this reads the originals.

    find /path/to/library -name '*.mkv' | ./lacingcheck.py
"""
import sys
import time
from collections import defaultdict

import mkvindex


def main():
    for line in sys.stdin.read().splitlines():
        path = line.strip()
        if not path:
            continue
        t0 = time.time()
        try:
            ix = mkvindex.index(path)
        except Exception as exc:                       # a spike, not a parser suite
            print(f"  {path.split('/')[-1][:48]:<50} ERROR {exc}")
            continue

        by_track = defaultdict(set)
        total = 0
        for num, track in ix["tracks"].items():
            total += len(track.samples)
            by_track[num].add("laced" if track.laced_blocks else "none")

        laced = {n: t for n, t in ix["tracks"].items() if t.laced_blocks}
        print(f"  {path.split('/')[-1][:46]:<48} walk {time.time() - t0:5.1f}s  "
              f"samples {total:>9,}  lacing: {'YES' if laced else 'no'}")
        for n, t in sorted(laced.items()):
            print(f"      track {n} ({t.codec}): {t.laced_blocks:,} laced blocks")


main()
