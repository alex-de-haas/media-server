#!/usr/bin/env python3
"""Serve the synthesised MP4 without ever writing it.

Bytes below the header length come from memory; everything above comes straight out
of the untouched .mkv at (offset - header length). No file is produced, nothing is
cached, and the whole of the response is assembled per request.

    ./serve.py <source.mkv> <port> [--entry dvh1|hvc1]
"""
import os
import re
import sys
import time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

import mkvindex
import mp4synth

USAGE = "usage: ./serve.py <source.mkv> <port> [--entry dvh1|hvc1]"

args = sys.argv[1:]
ENTRY = "dvh1"
if "--entry" in args:
    i = args.index("--entry")
    if i + 1 >= len(args) or args[i + 1] not in ("dvh1", "hvc1"):
        sys.exit(USAGE)
    ENTRY = args[i + 1]
    del args[i:i + 2]
if len(args) != 2 or not args[1].isdigit():
    sys.exit(USAGE)
SOURCE, PORT = args[0], int(args[1])
if not os.path.isfile(SOURCE):
    sys.exit(f"no such file: {SOURCE}")

t0 = time.time()
INDEX = mkvindex.index(SOURCE)
walk_seconds = time.time() - t0
WANT = [n for n, t in sorted(INDEX["tracks"].items()) if t.type in (1, 2)][:2]
HEADER, TOTAL, REPORT = mp4synth.build(INDEX, WANT, sample_entry=ENTRY)
HEADER_LEN = len(HEADER)

print(f"source      {SOURCE}")
print(f"index walk  {walk_seconds:.1f}s over {INDEX['file_size']:,} B")
print(f"tracks      {REPORT['tracks']}")
print(f"header      {HEADER_LEN:,} B ({HEADER_LEN / INDEX['file_size'] * 100:.4f}% of source)")
print(f"total       {TOTAL:,} B   entry={REPORT['sample_entry']}   DV={REPORT['dv']}")
print(f"serving on  http://0.0.0.0:{PORT}/movie.mp4", flush=True)

WINDOW = 8 << 20        # what one response is willing to hand over
REQUESTS = []


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _write_slice(self, start, end):
        """The whole synthesis: header from memory, media from the source file.

        Streamed rather than accumulated — an open-ended range over a 26 GB source
        would otherwise be built in memory in full.
        """
        if start < HEADER_LEN:
            self.wfile.write(HEADER[start:min(end + 1, HEADER_LEN)])
        if end >= HEADER_LEN:
            begin = max(start, HEADER_LEN) - HEADER_LEN
            remaining = end - HEADER_LEN - begin + 1
            with open(SOURCE, "rb") as f:
                f.seek(begin)
                while remaining > 0:
                    chunk = f.read(min(1 << 20, remaining))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    remaining -= len(chunk)

    def do_HEAD(self):
        self.send_response(200)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(TOTAL))
        self.end_headers()

    def do_GET(self):
        rng = self.headers.get("Range")
        REQUESTS.append((time.time(), rng))
        print(f"{self.path}  {rng}", flush=True)

        if not rng:
            start, end = 0, TOTAL - 1
            code = 200
        else:
            # A malformed or multi-range header must not take the handler down; it is
            # answered as if no range had been asked for.
            m = re.fullmatch(r"bytes=(\d*)-(\d*)", rng.strip())
            if m is None or not (m.group(1) or m.group(2)):
                self.send_error(416, "unsupported range")
                return
            start = int(m.group(1)) if m.group(1) else 0
            if start >= TOTAL:
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{TOTAL}")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            if m.group(2):
                # An explicit end is honoured in full. Truncating it is read as a
                # failed request, not as a smaller answer.
                end = max(start, min(int(m.group(2)), TOTAL - 1))
            else:
                # An open-ended range is a request for the rest of a 26 GB file, so
                # hand over a window and let the client come back.
                end = min(start + WINDOW - 1, TOTAL - 1)
            code = 206

        # AVFoundation refuses an undeclared length, so the total is always stated.
        self.send_response(code)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        if code == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{TOTAL}")
        self.send_header("Content-Length", str(end - start + 1))
        self.end_headers()
        try:
            self._write_slice(start, end)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def handle_one_request(self):
        try:
            super().handle_one_request()
        except (BrokenPipeError, ConnectionResetError):
            self.close_connection = True

    def log_message(self, *a):
        pass


ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
