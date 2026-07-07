#!/usr/bin/env python3
"""
Headless driver for the suite's dat tools (Counter, Validator, Bulk Header
Updater), printing the same structured output as the ParityRunner modes.

Usage:
  python run_tools.py count <folder>
  python run_tools.py validate <fileOrFolder>
  python run_tools.py bhu <target> <date> [field=value ...] [--clear field] [--fp]
  python run_tools.py merge <categoryRoot> <date>
  python run_tools.py pack <folder> <ext1,ext2>
  python run_tools.py extract <folder> <7zPath>
"""
import os
import sys
import zipfile
from pathlib import Path

SUITE_DIR = r"C:\Eggmansworld\Datfile_Creator_Suite"
sys.path.insert(0, SUITE_DIR)
import Eggmans_Datfile_Creator_Suite as suite  # noqa: E402


def mode_count(folder: str) -> int:
    files = suite._dv_collect_files(folder, single_mode=False)
    for fp in files:
        g, r, b, d, name, err = suite._scan_dat_counts(fp)
        rel = os.path.relpath(fp, folder).replace("\\", "/")
        print(f"count|{rel}|{name}|{g}|{r}|{b}|{d}|{err}")
    return 0


def mode_validate(path: str) -> int:
    files = suite._dv_collect_files(path, single_mode=os.path.isfile(path))
    total_issues = total_roms = 0
    for fp in files:
        print("file|" + os.path.basename(fp))
        issue_lines = []

        def post(text, tag=""):
            issue_lines.append(text.rstrip("\n"))

        issues, roms = suite._dv_validate_file(fp, post, lambda: False)
        for line in issue_lines:
            print(line)
        print(f"result|{issues}|{roms}")
        total_issues += issues
        total_roms += roms
    print(f"total|{len(files)}|{total_roms}|{total_issues}")
    return 0


def mode_bhu(argv: list) -> int:
    target, date = argv[0], argv[1]
    field_values = {f: None for f, _ in suite._BHU_OPTIONAL_FIELDS}
    add_fp = False
    i = 2
    while i < len(argv):
        a = argv[i]
        if a == "--fp":
            add_fp = True
            i += 1
        elif a == "--clear" and i + 1 < len(argv):
            field_values[argv[i + 1]] = ""
            i += 2
        elif "=" in a:
            k, v = a.split("=", 1)
            field_values[k] = v
            i += 1
        else:
            i += 1

    for fp in sorted(suite._bhu_iter_datfiles(target)):
        d = suite._bhu_update_file(Path(fp), date, field_values, add_fp)
        rel_after = os.path.relpath(d["path_after"], target).replace("\\", "/")
        print(f"bhu|{rel_after}"
              + f"|fn={d['fn_date_before'] or 'None'}>{d['fn_date_after'] or 'None'}"
              + f"|hdr={d['hdr_date_before'] or 'None'}>{d['hdr_date_after'] or 'None'}"
              + f"|added={','.join(d['fields_added'])}"
              + f"|updated={','.join(d['fields_updated'])}"
              + f"|cleared={','.join(d['fields_cleared'])}"
              + f"|renamed={1 if d['renamed'] else 0}|content={1 if d['content_updated'] else 0}"
              + f"|warn={';'.join(d['warnings'])}")
    return 0


def mode_merge(root: str, date: str) -> int:
    category = os.path.basename(root.rstrip("\\/"))
    for job in suite._dm_scan_for_merge(root):
        if job["action"] != "merge":
            continue
        merged_games, header, err = suite._dm_collect_games(job["path"], job["deeper"])
        if err:
            print(f"merge|{job['name']}|ERROR|{err}")
            continue
        dat_name = category + " - " + job["name"]
        out_fn = suite.make_dat_filename(dat_name, date)
        out_path = os.path.join(job["path"], out_fn)
        suite._dm_write_merged_dat(out_path, dat_name, merged_games, header, date)
        rom_total = sum(len(v) for v in merged_games.values())
        rel = os.path.relpath(out_path, root).replace("\\", "/")
        print(f"merge|{job['name']}|{len(merged_games)}|{rom_total}|{rel}")
    return 0


def mode_pack(folder: str, exts_csv: str) -> int:
    exts = ["." + e.strip().lstrip(".").lower() for e in exts_csv.split(",") if e.strip()]
    fp_root = Path(folder)
    files = sorted({p for ext in exts for p in fp_root.rglob(f"*{ext}")})
    for fp in files:
        zip_path = fp.with_suffix(".zip")
        if zip_path.exists():
            continue
        with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_STORED, allowZip64=True) as zf:
            zf.write(fp, fp.name)
        # verify + delete original (mirror the suite's default flow)
        with zipfile.ZipFile(zip_path, "r") as zf:
            if zf.testzip() is None and zf.getinfo(fp.name).file_size == fp.stat().st_size:
                fp.unlink()
    for zip_path in sorted(fp_root.rglob("*.zip")):
        with zipfile.ZipFile(zip_path, "r") as zf:
            for info in zf.infolist():
                rel = os.path.relpath(zip_path, folder).replace("\\", "/")
                print(f"zip|{rel}|{info.filename}|{info.file_size}"
                      f"|{info.CRC & 0xFFFFFFFF:08x}|{info.compress_type}")
    return 0


def mode_extract(folder: str, sevenzip: str) -> int:
    import collections
    src_root = Path(folder)
    exts = {".zip", ".7z", ".rar"}
    initial = sorted({p for ext in exts for p in src_root.rglob(f"*{ext}")})
    queue = collections.deque(initial)
    queued = set(initial)
    while queue:
        arc = queue.popleft()
        mode, _ = suite._au_classify(arc, sevenzip)
        if mode == "bad":
            continue
        try:
            rel_parent = arc.relative_to(src_root).parent
        except ValueError:
            rel_parent = Path(".")
        out_dir = arc.parent if mode == "single" \
            else arc.parent / suite._au_sanitize(arc.stem)
        if mode == "single":
            suite._au_extract_single(arc, out_dir, sevenzip)
        else:
            suite._au_extract_to_folder(arc, out_dir, sevenzip)
    for f in sorted(src_root.rglob("*")):
        if f.is_file():
            rel = os.path.relpath(f, folder).replace("\\", "/")
            print(f"tree|{rel}|{f.stat().st_size}")
    return 0


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__, file=sys.stderr)
        return 2
    mode = sys.argv[1]
    if mode == "count":
        return mode_count(sys.argv[2])
    if mode == "validate":
        return mode_validate(sys.argv[2])
    if mode == "bhu":
        return mode_bhu(sys.argv[2:])
    if mode == "merge":
        return mode_merge(sys.argv[2], sys.argv[3])
    if mode == "pack":
        return mode_pack(sys.argv[2], sys.argv[3])
    if mode == "extract":
        return mode_extract(sys.argv[2], sys.argv[3])
    print("Unknown mode: " + mode, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
