"""
category_report.py
Groups elements in a Perseus export by category and counts them.
Category ElementIds are resolved from the CATEGORY- delta entries in the export.

Usage:
    python category_report.py <export.json> [output.json]
"""

import argparse
import json
import sys
from collections import defaultdict
from datetime import datetime, timezone
import pandas as pd


def load(filepath):
    with open(filepath, "r", encoding="utf-8") as f:
        data = json.load(f)
    return data if isinstance(data, list) else data.get("elements", [])


def build_category_map(deltas):
    cat_map = {}
    for delta in deltas:
        element = delta.get("element", {})
        uid = element.get("unique_id", "")
        if uid.startswith("CATEGORY-"):
            eid = element.get("element_id")
            name = element.get("name")
            if eid is not None and name:
                cat_map[int(eid)] = name
    return cat_map


def get_category_id(element):
    for param in element.get("parameters", []):
        if param.get("name") == "Category" and param.get("value_type") == "ElementId":
            val = param.get("value")
            return int(val) if val is not None else None
    return None


def report(filepath, output_path=None):
    deltas = load(filepath)
    cat_map = build_category_map(deltas)

    counts = defaultdict(int)
    unresolved = defaultdict(int)

    for delta in deltas:
        element = delta.get("element", {})
        if element.get("unique_id", "").startswith("CATEGORY-"):
            continue
        cat_id = get_category_id(element)
        if cat_id is None:
            counts["(no category parameter)"] += 1
        elif cat_id in cat_map:
            counts[cat_map[cat_id]] += 1
        else:
            unresolved[cat_id] += 1

    rows = [{"category": name, "element_count": n} for name, n in counts.items()]
    df = pd.DataFrame(rows).sort_values("element_count", ascending=False).reset_index(drop=True)

    unresolved_rows = [{"category_id": eid, "element_count": n} for eid, n in sorted(unresolved.items())]

    print(f"\n{'='*50}")
    print(f"  {len(cat_map)} categories defined | {df['element_count'].sum()} elements | {len(df)} resolved categories")
    if unresolved:
        print(f"  {len(unresolved)} unresolved category IDs")
    print(f"{'='*50}\n")
    print(df.to_string(index=False))
    if unresolved_rows:
        print(f"\n--- Unresolved category IDs ({len(unresolved_rows)}) ---")
        print(pd.DataFrame(unresolved_rows).to_string(index=False))

    if output_path:
        report_data = {
            "meta": {
                "source_file": filepath,
                "generated_at": datetime.now(timezone.utc).isoformat(),
                "categories_defined": len(cat_map),
                "total_elements": int(df["element_count"].sum()),
            },
            "categories": rows,
            "unresolved": unresolved_rows,
        }
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(report_data, f, indent=2)
        print(f"\nJSON report written to: {output_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="Group elements in a Perseus export by category and count them.",
        epilog=(
            "Category ElementIds are resolved from the CATEGORY- delta entries in the export. "
            "Elements whose category ID cannot be resolved are listed separately."
        ),
    )
    parser.add_argument("export", help="Export JSON file to analyze")
    parser.add_argument("output", nargs="?", default=None, help="Output JSON report path (optional)")
    args = parser.parse_args()
    report(args.export, args.output)
