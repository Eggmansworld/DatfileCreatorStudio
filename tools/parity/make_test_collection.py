#!/usr/bin/env python3
"""
Build the deterministic parity test collection under parity-out/TestCollection.

Uses the RVZSTD test archives from test_rvzstd_archives/ (which contain
zero-byte files, empty folders, and nested empty folders) plus synthetic
Store/Deflate zips and loose files that exercise CP437 glyph translation,
Windows-forbidden character sanitising, and empty content.

NOTE: This script only COPIES existing RVZSTD archives. It never creates
zstd archives and never writes an "RVZSTD-" zip comment — that marker is
exclusively RomVault's.
"""
import os
import shutil
import sys
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(REPO, "test_rvzstd_archives")
DEST = os.path.join(REPO, "parity-out", "TestCollection")

RVZSTD = [
    "Acheton and Kingdom of Hamil (United Kingdom).zip",
    "Adventure Collection, The (United Kingdom) (Disk 1).zip",
    "Adventure Collection, The (United Kingdom) (Disk 2).zip",
    "Radikal Bikers (1998)[Gaelco PC,Atari License][TP].zip",
    "Space Invaders (1.05)(2012-02-09)(JPN)[Taito NESiCAxLive][TP].zip",
]


def det_bytes(n: int, seed: int) -> bytes:
    """Deterministic pseudo-random bytes (fixed across runs and machines)."""
    out = bytearray()
    x = seed & 0xFFFFFFFF
    while len(out) < n:
        x = (1103515245 * x + 12345) & 0xFFFFFFFF
        out += x.to_bytes(4, "little")
    return bytes(out[:n])


def main() -> int:
    if os.path.isdir(DEST):
        shutil.rmtree(DEST)
    os.makedirs(DEST)

    def copy_rvzstd(index: int, rel: str) -> None:
        src = os.path.join(SRC, RVZSTD[index])
        dst = os.path.join(DEST, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)

    # ── Folder A: rvzstd zips at two depths ─────────────────────────────
    copy_rvzstd(0, os.path.join("Folder A", RVZSTD[0]))
    copy_rvzstd(1, os.path.join("Folder A", RVZSTD[1]))
    copy_rvzstd(2, os.path.join("Folder A", "sub", RVZSTD[2]))

    # ── Folder B: rvzstd zip + nested container folders ─────────────────
    copy_rvzstd(3, os.path.join("Folder B", RVZSTD[3]))
    copy_rvzstd(4, os.path.join("Folder B", "Docs", "inner", RVZSTD[4]))

    # ── Folder C: loose files + synthetic zips ───────────────────────────
    folder_c = os.path.join(DEST, "Folder C")
    os.makedirs(os.path.join(folder_c, "original"))
    with open(os.path.join(folder_c, "game.ima"), "wb") as f:
        f.write(det_bytes(1024, 1))
    with open(os.path.join(folder_c, "empty.bin"), "wb"):
        pass  # zero-byte loose file
    with open(os.path.join(folder_c, "original", "v1.0.ima"), "wb") as f:
        f.write(det_bytes(2048, 2))

    # Stored zip: ZIP_STORED entries + zero-byte file + empty dir +
    # CP437 control byte in a name (0x03 -> heart glyph in the dat) +
    # Windows-forbidden characters in a name
    stored = os.path.join(folder_c, "stored.zip")
    with zipfile.ZipFile(stored, "w", zipfile.ZIP_STORED) as zf:
        zf.writestr("data1.bin", det_bytes(512, 3))
        zf.writestr("zero.bin", b"")
        zf.writestr("emptydir/", b"")
        zf.writestr("nested/emptier/", b"")
        zf.writestr("card\x03ace.bin", det_bytes(64, 4))
        zf.writestr("bad:name?.txt", b"forbidden chars")

    # ── Root-level loose content ─────────────────────────────────────────
    with open(os.path.join(DEST, "loose_readme.txt"), "w", encoding="utf-8") as f:
        f.write("loose file directly in the input root\n")

    root_zip = os.path.join(DEST, "RootZip.zip")
    with zipfile.ZipFile(root_zip, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("readme.txt", "deflate compressed entry\n" * 40)
        zf.writestr("sub/inner.dat", det_bytes(4096, 5))

    print("Test collection created at", DEST)
    return 0


if __name__ == "__main__":
    sys.exit(main())
