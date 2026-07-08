"""
generate_batch.py
Scans a directory for Revit (.rvt) files and writes a Perseus batch_task.json.

Usage:
  python tools/generate_batch.py D:\\RevitScrape\\models

  python tools/generate_batch.py D:\\RevitScrape\\models \\
      --output-dir D:\\RevitScrape\\output\\Perseus \\
      --export-mode FullSync \\
      --workset-regex "^A-" \\
      --skip-done D:\\RevitScrape\\output\\Perseus \\
      --out batch_task.json

Arguments:
  dir                   Directory to scan for .rvt files (recursive by default)

Options:
  --output-dir DIR      Value for output_dir in the JSON  [default: <dir>\\output]
  --export-mode MODE    IncrementalFile | FullSync | ...  [default: IncrementalFile]
  --workset-regex RE    Workset whitelist regex per model [default: ^A-]
  --no-connected        Set collect_connected_elements to false
  --category CAT        Add a perseus_categories entry (repeatable)
  --no-recurse          Only scan the top-level directory, not subdirectories
  --skip-done DIR       Skip .rvt files whose output already exists in DIR.
                        Matches by stripping the _detached suffix that Perseus
                        appends to output JSON filenames (case-insensitive).
  --out FILE            Write JSON to FILE instead of stdout
  --timestamp TS        Override ISO timestamp  [default: now]
  --session-iteration-cap N
                        Max loop iterations per Revit session.
                        0 = unlimited, omit = default (50).
"""

import argparse
import datetime
import json
import os
import sys


def find_rvt_files(root, recurse):
    matches = []
    if recurse:
        for dirpath, _, filenames in os.walk(root):
            for fname in filenames:
                if fname.lower().endswith(".rvt"):
                    matches.append(os.path.join(dirpath, fname))
    else:
        for fname in os.listdir(root):
            if fname.lower().endswith(".rvt"):
                full = os.path.join(root, fname)
                if os.path.isfile(full):
                    matches.append(full)
    return sorted(matches)


def build_done_set(done_dir):
    """Return a set of lowercase stems for already-processed models.

    Perseus names output files as <model-stem>_detached.json, so we strip
    that suffix to recover the original model stem for comparison.
    """
    done = set()
    for fname in os.listdir(done_dir):
        lower = fname.lower()
        if lower.endswith("_detached.json"):
            stem = lower[: -len("_detached.json")]
        elif lower.endswith(".json"):
            stem = lower[: -len(".json")]
        else:
            continue
        done.add(stem)
    return done


def main():
    ap = argparse.ArgumentParser(
        description="Generate a Perseus batch_task.json from a directory of .rvt files",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("dir", metavar="DIR",
                    help="Directory to scan for .rvt files")
    ap.add_argument("--output-dir", metavar="DIR",
                    help="output_dir value in the JSON (default: <dir>\\output)")
    ap.add_argument("--export-mode", default="IncrementalFile", metavar="MODE",
                    help="export_mode value (default: IncrementalFile)")
    ap.add_argument("--workset-regex", default="^A-", metavar="RE",
                    help="workset_whitelist_regex per model entry (default: ^A-)")
    ap.add_argument("--no-connected", action="store_true",
                    help="Set collect_connected_elements to false")
    ap.add_argument("--category", metavar="CAT", action="append", dest="categories",
                    help="Add a perseus_categories entry (repeatable)")
    ap.add_argument("--no-recurse", action="store_true",
                    help="Only scan the top-level directory")
    ap.add_argument("--skip-done", metavar="DIR",
                    help="Skip models whose output JSON already exists in DIR "
                         "(matches <stem>_detached.json, case-insensitive)")
    ap.add_argument("--out", metavar="FILE",
                    help="Write JSON to FILE (default: print to stdout)")
    ap.add_argument("--timestamp", metavar="TS",
                    help="Override ISO timestamp (default: now)")
    ap.add_argument("--session-iteration-cap", type=int, metavar="N",
                    help="session_iteration_cap in JSON: 0=unlimited, omit=default (50)")
    args = ap.parse_args()

    scan_dir = os.path.abspath(args.dir)
    if not os.path.isdir(scan_dir):
        print(f"[error] not a directory: {scan_dir}", file=sys.stderr)
        sys.exit(1)

    output_dir = args.output_dir or os.path.join(scan_dir, "output")
    timestamp = args.timestamp or datetime.datetime.now().strftime("%Y-%m-%dT%H:%M:%S")
    categories = args.categories or []
    collect_connected = not args.no_connected

    rvt_files = find_rvt_files(scan_dir, recurse=not args.no_recurse)
    if not rvt_files:
        print(f"[warn] no .rvt files found in {scan_dir}", file=sys.stderr)

    if args.skip_done:
        done_dir = os.path.abspath(args.skip_done)
        if not os.path.isdir(done_dir):
            print(f"[error] --skip-done is not a directory: {done_dir}", file=sys.stderr)
            sys.exit(1)
        done_set = build_done_set(done_dir)
        before = len(rvt_files)
        rvt_files = [
            p for p in rvt_files
            if os.path.splitext(os.path.basename(p))[0].lower() not in done_set
        ]
        skipped = before - len(rvt_files)
        if skipped:
            print(f"[info] skipped {skipped} already-processed model(s)", file=sys.stderr)

    batch = {
        "timestamp": timestamp,
        "export_mode": args.export_mode,
        "output_dir": output_dir,
        "models_to_process": [
            {
                "local_path": path,
                "perseus_categories": categories,
                "collect_connected_elements": collect_connected,
                "workset_whitelist_regex": args.workset_regex,
            }
            for path in rvt_files
        ],
    }
    if args.session_iteration_cap is not None:
        batch["session_iteration_cap"] = args.session_iteration_cap

    output = json.dumps(batch, indent=4)

    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(output)
        print(f"Wrote {len(rvt_files)} model(s) to {args.out}", file=sys.stderr)
    else:
        print(output)


if __name__ == "__main__":
    main()
