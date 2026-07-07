#!/usr/bin/env python3
"""
Headless driver for the suite's dat tools (Counter, Validator, Bulk Header
Updater), printing the same structured output as the ParityRunner modes.

Usage:
  python run_tools.py count <folder>
  python run_tools.py validate <fileOrFolder>
  python run_tools.py bhu <target> <date> [field=value ...] [--clear field] [--fp]
"""
import os
import sys
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
    print("Unknown mode: " + mode, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
