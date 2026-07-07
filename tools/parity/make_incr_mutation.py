#!/usr/bin/env python3
"""
Build the mutated collection used by the incremental-update parity scenarios.

Copies parity-out/TestCollection to parity-out/incr/TestCollection (same
basename, so dat-name matching lines up), then:
  - adds a new deflate zip in Folder A           (new item -> hashed)
  - deletes the Radikal Bikers zip from Folder B (entry removed from dat)
  - rebuilds Folder C/stored.zip with an extra entry (changed CRC -> rehash)
  - adds a loose file in Folder C                (Mixed: new item)
  - deletes Folder C/empty.bin                   (Mixed: entry removed)
Everything else is untouched and must be carried forward by the incremental
engines without rehashing.
"""
import os
import shutil
import sys
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(REPO, "parity-out", "TestCollection")
DEST = os.path.join(REPO, "parity-out", "incr", "TestCollection")


def det_bytes(n: int, seed: int) -> bytes:
    out = bytearray()
    x = seed & 0xFFFFFFFF
    while len(out) < n:
        x = (1103515245 * x + 12345) & 0xFFFFFFFF
        out += x.to_bytes(4, "little")
    return bytes(out[:n])


def main() -> int:
    if not os.path.isdir(SRC):
        print("Run make_test_collection.py first —", SRC, "not found", file=sys.stderr)
        return 1
    if os.path.isdir(DEST):
        shutil.rmtree(DEST)
    shutil.copytree(SRC, DEST)

    # New zip in Folder A → must be hashed fresh
    new_zip = os.path.join(DEST, "Folder A", "New Game (2026).zip")
    with zipfile.ZipFile(new_zip, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("new_game.rom", det_bytes(2048, 10))

    # Deleted zip in Folder B → entry must disappear from the updated dat
    os.remove(os.path.join(DEST, "Folder B",
                           "Radikal Bikers (1998)[Gaelco PC,Atari License][TP].zip"))

    # Changed zip in Folder C → CRC fingerprint mismatch, must be rehashed
    stored = os.path.join(DEST, "Folder C", "stored.zip")
    os.remove(stored)
    with zipfile.ZipFile(stored, "w", zipfile.ZIP_STORED) as zf:
        zf.writestr("data1.bin", det_bytes(512, 3))
        zf.writestr("zero.bin", b"")
        zf.writestr("emptydir/", b"")
        zf.writestr("nested/emptier/", b"")
        zf.writestr("card\x03ace.bin", det_bytes(64, 4))
        zf.writestr("bad:name?.txt", b"forbidden chars")
        zf.writestr("added_later.bin", det_bytes(256, 11))  # the change

    # Mixed-mode mutations: one new loose file, one deleted loose file
    with open(os.path.join(DEST, "Folder C", "new_file.bin"), "wb") as f:
        f.write(det_bytes(300, 12))
    os.remove(os.path.join(DEST, "Folder C", "empty.bin"))

    print("Mutated collection created at", DEST)
    return 0


if __name__ == "__main__":
    sys.exit(main())
