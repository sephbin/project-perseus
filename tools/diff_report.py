"""
diff_report.py
Compares two Perseus full-sync JSON exports and reports added, removed, and
modified elements with per-parameter and per-geometry change details.

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
    """Returns dict keyed by (name, param_id) → full param object."""
    result = {}
    for p in element.get("parameters", []):
        name = p.get("name")
        if name:
            key = (name, p.get("param_id"))
            result[key] = p
    return result


def geometry_map(element):
    """Returns dict keyed by geometry_type → geometry object."""
    result = {}
    for g in element.get("geometries") or []:
        gtype = g.get("geometry_type")
        if gtype:
            result[gtype] = g.get("geometry")
    return result


def coords_equal(a, b):
    """Recursively compare coordinate arrays with float tolerance."""
    if a is None and b is None:
        return True
    if a is None or b is None:
        return False
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            return False
        return all(coords_equal(x, y) for x, y in zip(a, b))
    return values_equal(a, b)


def geometries_equal(a, b):
    if a is None and b is None:
        return True
    if a is None or b is None:
        return False
    if a.get("type") != b.get("type"):
        return False
    if not coords_equal(a.get("coordinates"), b.get("coordinates")):
        return False
    if not values_equal(a.get("rotation"), b.get("rotation")):
        return False
    return True


def geometry_summary(geom):
    """Short human-readable description of a geometry for console output."""
    if geom is None:
        return "None"
    gtype = geom.get("type")
    coords = geom.get("coordinates")
    if gtype == "Point" and coords:
        return f"Point({coords[0]:.3f}, {coords[1]:.3f}, {coords[2]:.3f})"
    if gtype == "RevitLocationPoint" and coords:
        rot = geom.get("rotation", 0)
        return f"RevitPoint({coords[0]:.3f}, {coords[1]:.3f}, {coords[2]:.3f}, rot={rot:.4f}rad)"
    if gtype == "LineString" and coords:
        return f"LineString({len(coords)} pts)"
    if gtype == "Polygon" and coords:
        total_pts = sum(len(ring) for ring in coords)
        return f"Polygon({len(coords)} ring(s), {total_pts} pts)"
    return str(geom)


def diff(old, new):
    old_ids = set(old)
    new_ids = set(new)

    added   = [new[uid] for uid in sorted(new_ids - old_ids)]
    removed = [old[uid] for uid in sorted(old_ids - new_ids)]

    modified = []
    for uid in sorted(old_ids & new_ids):
        old_elem, new_elem = old[uid], new[uid]
        old_params = param_map(old_elem)
        new_params = param_map(new_elem)
        old_geoms  = geometry_map(old_elem)
        new_geoms  = geometry_map(new_elem)

        changes = []

        # Name change
        if old_elem.get("name") != new_elem.get("name"):
            changes.append({
                "parameter":     "(name)",
                "param_id":      None,
                "param_id_type": "synthetic",
                "value_type":    "String",
                "old_value":     old_elem.get("name"),
                "new_value":     new_elem.get("name"),
            })

        # Parameter changes
        for key in sorted(set(old_params) | set(new_params), key=lambda k: k[0]):
            op  = old_params.get(key)
            np_ = new_params.get(key)
            ov  = op.get("value")  if op  else None
            nv  = np_.get("value") if np_ else None
            if not values_equal(ov, nv):
                param_name, param_id = key
                sample = op or np_
                changes.append({
                    "parameter":     param_name,
                    "param_id":      param_id,
                    "param_id_type": sample.get("param_id_type"),
                    "value_type":    sample.get("value_type"),
                    "old_value":     ov,
                    "new_value":     nv,
                })

        # Geometry changes
        for gtype in sorted(set(old_geoms) | set(new_geoms)):
            og = old_geoms.get(gtype)
            ng = new_geoms.get(gtype)
            if not geometries_equal(og, ng):
                changes.append({
                    "parameter":     f"(geometry:{gtype})",
                    "param_id":      None,
                    "param_id_type": "geometry",
                    "value_type":    "geometry",
                    "old_value":     og,
                    "new_value":     ng,
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

    total_param_changes = sum(
        sum(1 for c in m["changes"] if c["param_id_type"] != "geometry")
        for m in modified
    )
    total_geom_changes = sum(
        sum(1 for c in m["changes"] if c["param_id_type"] == "geometry")
        for m in modified
    )

    print(f"\n{'='*65}")
    print(f"  DIFFERENTIAL REPORT")
    print(f"  A (old): {path_a}")
    print(f"  B (new): {path_b}")
    print(f"{'='*65}")
    print(f"  Elements in A     : {len(old)}")
    print(f"  Elements in B     : {len(new)}")
    print(f"  Added             : {len(added)}")
    print(f"  Removed           : {len(removed)}")
    print(f"  Modified          : {len(modified)}")
    print(f"  Parameter changes : {total_param_changes}")
    print(f"  Geometry changes  : {total_geom_changes}")
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
                is_geom = c["param_id_type"] == "geometry"
                rows.append({
                    "unique_id":     uid,
                    "element_name":  name,
                    "parameter":     c["parameter"],
                    "param_id":      c["param_id"],
                    "param_id_type": c["param_id_type"],
                    "value_type":    c.get("value_type"),
                    "old_value":     geometry_summary(c["old_value"]) if is_geom else c["old_value"],
                    "new_value":     geometry_summary(c["new_value"]) if is_geom else c["new_value"],
                })
        print(pd.DataFrame(rows).to_string(index=False))

    if output_path:
        report_data = {
            "meta": {
                "file_a":        path_a,
                "file_b":        path_b,
                "generated_at":  datetime.now(timezone.utc).isoformat(),
                "elements_in_a": len(old),
                "elements_in_b": len(new),
            },
            "summary": {
                "added":                   len(added),
                "removed":                 len(removed),
                "modified":                len(modified),
                "total_parameter_changes": total_param_changes,
                "total_geometry_changes":  total_geom_changes,
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
