"""
element_lookup.py
Given a list of element IDs, retrieves the full element objects from a Perseus
JSON export.

The ID list can be supplied as:
  - A plain text file  — one element_id per line
  - A JSON file        — either a flat array ["123", "456", …]
                         or a user_report output containing an "element_ids" key

Usage:
    python element_lookup.py <export.json> <ids.txt|ids.json> [output.json]
"""

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
import pandas as pd


def load_export(filepath):
    with open(filepath, "r", encoding="utf-8") as f:
        data = json.load(f)
    deltas = data if isinstance(data, list) else data.get("elements", [])
    elements = {}
    for delta in deltas:
        elem = delta.get("element", {})
        eid = elem.get("element_id")
        if eid is not None:
            elements[str(eid)] = elem
    return elements


def load_ids(filepath):
    path = Path(filepath)
    if path.suffix.lower() == ".json":
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, list):
            return [str(i) for i in data]
        if isinstance(data, dict):
            ids = data.get("element_ids") or []
            if not ids:
                # user_report format: look inside users list
                for user in data.get("users", []):
                    ids.extend(user.get("element_ids") or [])
            return [str(i) for i in ids]
        raise ValueError(f"Unrecognised JSON structure in {filepath}")
    else:
        with open(path, "r", encoding="utf-8") as f:
            return [line.strip() for line in f if line.strip()]


def report(export_path, ids_path, output_path=None):
    elements = load_export(export_path)
    target_ids = load_ids(ids_path)

    found = []
    missing = []
    for eid in target_ids:
        if eid in elements:
            found.append(elements[eid])
        else:
            missing.append(eid)

    print(f"\n{'='*55}")
    print(f"  Requested : {len(target_ids)}")
    print(f"  Found     : {len(found)}")
    print(f"  Missing   : {len(missing)}")
    print(f"{'='*55}\n")

    if found:
        rows = [
            {
                "element_id": e.get("element_id"),
                "name":       e.get("name"),
                "last_edited_by": e.get("last_edited_by"),
            }
            for e in found
        ]
        print(pd.DataFrame(rows).to_string(index=False))

    if missing:
        print(f"\n--- Not found in export ({len(missing)}) ---")
        for eid in missing:
            print(f"  {eid}")

    if output_path:
        report_data = {
            "meta": {
                "export_file": export_path,
                "ids_file":    ids_path,
                "generated_at": datetime.now(timezone.utc).isoformat(),
                "requested": len(target_ids),
                "found":     len(found),
                "missing":   len(missing),
            },
            "elements": found,
            "missing_ids": missing,
        }
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(report_data, f, indent=2)
        print(f"\nJSON report written to: {output_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="Look up full element objects by element_id in a Perseus JSON export.",
        epilog=(
            "The ID list file can be a plain text file (one ID per line) or a JSON file "
            "(flat array, or a user_report output with an element_ids key)."
        ),
    )
    parser.add_argument("export", help="Perseus export JSON file")
    parser.add_argument("ids",    help="File containing element IDs to look up (.txt or .json)")
    parser.add_argument("output", nargs="?", default=None, help="Output JSON report path (optional)")
    args = parser.parse_args()
    report(args.export, args.ids, args.output)
