"""
diff_report.py
Compares two Perseus full-sync JSON exports and reports added, removed, and
modified elements with per-parameter change details.

Best used with two full-sync exports. Incremental exports only contain changed
elements, so absent elements mean "unchanged", not "deleted".

Usage:
    python diff_report.py <old.json> <new.json> [output.json]
"""

import json
import math
import sys
from datetime import datetime, timezone
import pandas as pd


def values_equal(a, b):
    """Exact equality for non-numerics; tolerant float comparison for numbers."""
    if a == b:
        return True
    try:
        return math.isclose(float(a), float(b), rel_tol=1e-6, abs_tol=1e-9)
    except (TypeError, ValueError):
        return False


def load(filepath):
    with open(filepath, "r", encoding="utf-8") as f:
        data = json.load(f)
    deltas = data if isinstance(data, list) else data.get("elements", [])
    elements = {}
    for delta in deltas:
        elem = delta.get("element", {})
        uid = elem.get("unique_id")
        if uid and not uid.startswith("CATEGORY-"):
            elements[uid] = elem
    return elements


def param_map(element):
    result = {}
    for p in element.get("parameters", []):
        name = p.get("name")
        if name:
            # Key on (name, guid, definition_id) so same-name params with different
            # identifiers are not collapsed into each other
            guid = p.get("guid")
            def_id = p.get("definition_id")
            key = (name, guid, def_id)
            result[key] = p.get("value")
    return result


def diff(old, new):
    old_ids = set(old)
    new_ids = set(new)

    added   = [new[uid] for uid in sorted(new_ids - old_ids)]
    removed = [old[uid] for uid in sorted(old_ids - new_ids)]

    modified = []
    for uid in sorted(old_ids & new_ids):
        old_elem, new_elem = old[uid], new[uid]
        old_params, new_params = param_map(old_elem), param_map(new_elem)

        changes = []

        if old_elem.get("name") != new_elem.get("name"):
            changes.append({
                "parameter":    "(name)",
                "guid":         None,
                "definition_id": None,
                "old_value":    old_elem.get("name"),
                "new_value":    new_elem.get("name"),
            })

        for key in sorted(set(old_params) | set(new_params), key=lambda k: k[0]):
            ov = old_params.get(key)
            nv = new_params.get(key)
            if not values_equal(ov, nv):
                param_name, guid, def_id = key
                changes.append({
                    "parameter":     param_name,
                    "guid":          guid,
                    "definition_id": def_id,
                    "old_value":     ov,
                    "new_value":     nv,
                })

        if changes:
            modified.append({"element": new_elem, "changes": changes})

    return added, removed, modified


def elem_row(e):
    return {
        "unique_id":  e.get("unique_id"),
        "name":       e.get("name"),
        "element_id": e.get("element_id"),
    }


def report(path_a, path_b, output_path=None):
    old = load(path_a)
    new = load(path_b)
    added, removed, modified = diff(old, new)

    total_changes = sum(len(m["changes"]) for m in modified)

    print(f"\n{'='*65}")
    print(f"  DIFFERENTIAL REPORT")
    print(f"  A (old): {path_a}")
    print(f"  B (new): {path_b}")
    print(f"{'='*65}")
    print(f"  Elements in A : {len(old)}")
    print(f"  Elements in B : {len(new)}")
    print(f"  Added         : {len(added)}")
    print(f"  Removed       : {len(removed)}")
    print(f"  Modified      : {len(modified)}  ({total_changes} parameter changes)")
    print(f"{'='*65}")

    if added:
        print(f"\n--- ADDED ({len(added)}) ---")
        print(pd.DataFrame([elem_row(e) for e in added]).to_string(index=False))

    if removed:
        print(f"\n--- REMOVED ({len(removed)}) ---")
        print(pd.DataFrame([elem_row(e) for e in removed]).to_string(index=False))

    if modified:
        print(f"\n--- MODIFIED ({len(modified)} elements) ---")
        rows = []
        for m in modified:
            uid  = m["element"].get("unique_id")
            name = m["element"].get("name")
            for c in m["changes"]:
                rows.append({"unique_id": uid, "element_name": name, **c})
        print(pd.DataFrame(rows).to_string(index=False))

    if output_path:
        report_data = {
            "meta": {
                "file_a":       path_a,
                "file_b":       path_b,
                "generated_at": datetime.now(timezone.utc).isoformat(),
                "elements_in_a": len(old),
                "elements_in_b": len(new),
            },
            "summary": {
                "added":                   len(added),
                "removed":                 len(removed),
                "modified":                len(modified),
                "total_parameter_changes": total_changes,
            },
            "added":    [elem_row(e) for e in added],
            "removed":  [elem_row(e) for e in removed],
            "modified": [
                {
                    "unique_id":    m["element"].get("unique_id"),
                    "element_name": m["element"].get("name"),
                    "element_id":   m["element"].get("element_id"),
                    "changes":      m["changes"],
                }
                for m in modified
            ],
        }
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(report_data, f, indent=2)
        print(f"\nJSON report written to: {output_path}")


if __name__ == "__main__":
    if len(sys.argv) < 3:
        a   = input("File A (older): ").strip()
        b   = input("File B (newer): ").strip()
        out = input("Output JSON path (leave blank to skip): ").strip() or None
    else:
        a   = sys.argv[1]
        b   = sys.argv[2]
        out = sys.argv[3] if len(sys.argv) > 3 else None
    report(a, b, out)
