#!/usr/bin/env python3
"""
Headless driver for the Python suite's process() engine, used by the parity
harness. Loads the same settings JSON the C# ParityRunner consumes and runs
one generation pass.

Usage: python run_python.py --settings <file.json> [--suite <dir>]
"""
import json
import os
import queue
import sys
import threading

SUITE_DIR = r"C:\Eggmansworld\Datfile_Creator_Suite"


def main() -> int:
    args = sys.argv[1:]
    settings_path = None
    suite_dir = SUITE_DIR
    i = 0
    while i < len(args):
        if args[i] == "--settings" and i + 1 < len(args):
            settings_path = args[i + 1]
            i += 2
        elif args[i] == "--suite" and i + 1 < len(args):
            suite_dir = args[i + 1]
            i += 2
        else:
            i += 1

    if not settings_path or not os.path.isfile(settings_path):
        print("Usage: run_python.py --settings <file.json>", file=sys.stderr)
        return 2

    sys.path.insert(0, suite_dir)
    import Eggmans_Datfile_Creator_Suite as suite

    with open(settings_path, encoding="utf-8") as f:
        d = json.load(f)
    s = suite.Settings.from_dict(d)
    s.date = d.get("date", "")

    q: "queue.Queue" = queue.Queue()
    done_msg = {}

    def drain():
        while True:
            msg = q.get()
            kind = msg[0]
            if kind == "status":
                print("[status]", msg[1])
            elif kind == "item_error":
                print("[err]", msg[1], "::", msg[2])
            elif kind == "dat_written":
                print("[dat]", msg[1])
            elif kind == "done":
                done_msg["msg"] = msg
                return

    t = threading.Thread(target=drain, daemon=True)
    t.start()
    suite.process(s, q, threading.Event(), threading.Event(), None)
    t.join(timeout=30)

    if "msg" not in done_msg:
        print("[error] engine did not report done", file=sys.stderr)
        return 1

    _, ok, errors, done_items, total_items, written, elapsed, _ = done_msg["msg"]
    for e in errors:
        print("[error]", e)
    print(f"[done] ok={ok} items={done_items}/{total_items} dats={written} elapsed={elapsed:.1f}s")
    return 0 if ok and not errors else 1


if __name__ == "__main__":
    sys.exit(main())
