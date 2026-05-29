"""
user_report.py
Reports all last_edited_by usernames found in a Perseus JSON export,
with element counts per user.  For any user with fewer than 100 elements,
the list of element unique_ids is included in the output.

Usage:
    python user_report.py <export.json> [output.json]
"""

import argparse
import json
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
import pandas as pd


ELEMENT_LIST_THRESHOLD = 100


def load(filepath):
    with open(filepath, "r", encoding="utf-8") as f:
        data = json.load(f)
    return data if isinstance(data, list) else data.get("elements", [])


def collect(deltas):
    counts = defaultdict(int)
    element_ids = defaultdict(list)

    for delta in deltas:
        element = delta.get("element", {})
        uid = element.get("unique_id", "")
        if uid.startswith("CATEGORY-"):
            continue
        user = element.get("last_edited_by") or "(unknown)"
        counts[user] += 1
        if counts[user] <= ELEMENT_LIST_THRESHOLD:
            element_ids[user].append(element.get("element_id"))

    return counts, element_ids


def report(filepath, output_path=None):
    deltas = load(filepath)
    counts, element_ids = collect(deltas)

    rows = [
        {"username": user, "element_count": n}
        for user, n in sorted(counts.items(), key=lambda x: -x[1])
    ]
    df = pd.DataFrame(rows)

    print(f"\n{'='*55}")
    print(f"  {len(counts)} unique user(s)  |  {df['element_count'].sum()} total elements")
    print(f"  Source: {filepath}")
    print(f"{'='*55}\n")
    print(df.to_string(index=False))

    sparse = {u: element_ids[u] for u in counts if counts[u] < ELEMENT_LIST_THRESHOLD}
    if sparse:
        print(f"\n--- Users with < {ELEMENT_LIST_THRESHOLD} elements (element IDs listed) ---")
        for user, ids in sorted(sparse.items(), key=lambda x: counts[x[0]]):
            print(f"\n  {user}  ({counts[user]} element(s)):")
            for uid in ids:
                print(f"    {uid}")

    if output_path:
        report_data = {
            "meta": {
                "source_file": filepath,
                "generated_at": datetime.now(timezone.utc).isoformat(),
                "total_users": len(counts),
                "total_elements": int(df["element_count"].sum()),
                "element_list_threshold": ELEMENT_LIST_THRESHOLD,
            },
            "users": [
                {
                    "username": user,
                    "element_count": counts[user],
                    "element_ids": element_ids[user] if counts[user] < ELEMENT_LIST_THRESHOLD else None,
                }
                for user in sorted(counts, key=lambda u: -counts[u])
            ],
        }
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(report_data, f, indent=2)
        print(f"\nJSON report written to: {output_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="Report last_edited_by usernames in a Perseus JSON export.",
        epilog=(
            f"Users with fewer than {ELEMENT_LIST_THRESHOLD} elements also have their "
            "element unique_ids listed. In the JSON output, element_ids is null for "
            "users at or above the threshold."
        ),
    )
    parser.add_argument("export", help="Export JSON file to analyze")
    parser.add_argument("output", nargs="?", default=None, help="Output JSON report path (optional)")
    args = parser.parse_args()
    report(args.export, args.output)
