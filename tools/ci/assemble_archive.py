#!/usr/bin/env python3
"""Reassemble the split game archive from the game-archive branch and verify it.

Usage: assemble_archive.py <archive dir with *.part* and SHA256SUMS> <output file>
"""
import glob
import hashlib
import os
import sys


def main():
    src, out = sys.argv[1], sys.argv[2]
    parts = sorted(glob.glob(os.path.join(src, "*.7z.part*")))
    if not parts:
        sys.exit("no archive parts in " + src)
    expected = None
    with open(os.path.join(src, "SHA256SUMS"), encoding="utf-8") as f:
        first = f.readline().split()
        if first:
            expected = first[0].lower()
    h = hashlib.sha256()
    with open(out, "wb") as o:
        for p in parts:
            with open(p, "rb") as f:
                while True:
                    chunk = f.read(1 << 22)
                    if not chunk:
                        break
                    h.update(chunk)
                    o.write(chunk)
    got = h.hexdigest()
    print("%d parts -> %s (%d bytes) sha256 %s" % (len(parts), out, os.path.getsize(out), got))
    if expected and got != expected:
        sys.exit("checksum mismatch, expected " + expected)


if __name__ == "__main__":
    main()
