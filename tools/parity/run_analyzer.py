#!/usr/bin/env python3
"""
Headless driver for the suite's folder structure analyzer, printing the same
structured findings format as `ParityRunner --analyze` for byte comparison.

Usage: python run_analyzer.py <folder> <mixed|zipped> [--suite <dir>]
"""
import sys

SUITE_DIR = r"C:\Eggmansworld\Datfile_Creator_Suite"


def main() -> int:
    args = sys.argv[1:]
    suite_dir = SUITE_DIR
    if "--suite" in args:
        i = args.index("--suite")
        suite_dir = args[i + 1]
        del args[i:i + 2]
    if len(args) < 2:
        print("Usage: run_analyzer.py <folder> <mixed|zipped>", file=sys.stderr)
        return 2

    folder, dat_type = args[0], args[1]
    sys.path.insert(0, suite_dir)
    import Eggmans_Datfile_Creator_Suite as suite

    f = suite.analyze_folder_structure(folder, dat_type)
    ps = suite._collect_path_lengths(folder)

    rec = f.get("recommendation", {})
    print("top_folders=" + str(f["top_folders"]))
    print("total_items=" + str(f["total_items"]))
    print("max_depth=" + str(f["max_depth"]))
    print("flat_games=" + str(f.get("folders_flat_games", 0)))
    print("with_direct=" + str(f["folders_with_direct_items"]))
    print("containers=" + str(f["folders_as_containers"]))
    print("nested=" + str(f["folders_with_nested_subdirs"]))
    print("empty=" + str(f["folders_empty"]))
    print("histogram=" + ",".join(
        f"{d}:{f['depth_histogram'][d]}" for d in sorted(f["depth_histogram"])))
    for note in f["notes"]:
        print("note=" + note)
    print("rec_gen=" + rec.get("gen_mode", ""))
    print("rec_structure=" + rec.get("structure", ""))
    print("rec_confidence=" + rec.get("confidence", ""))
    print("rec_summary=" + rec.get("summary", ""))
    for d in rec.get("detail", []):
        print("detail=" + d)
    print("path_total=" + str(ps["total_paths"]))
    print("path_max=" + str(ps["max_path_len"]))
    print("warn_count=" + str(ps["warn_count"]))
    print("crit_count=" + str(ps["crit_count"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
