"""
diff_report.py
Compares two Perseus full-sync JSON exports and reports added, removed, and
modified elements with per-parameter and per-geometry change details.

Best used with two full-sync exports. Incremental exports only contain changed
elements, so absent elements mean "unchanged", not "deleted".

Usage:
    python diff_report.py <old.json> <new.json> [output.json]
"""

import argparse
import json
import math
import sys
from datetime import datetime, timezone
import pandas as pd


# Revit internal units are decimal feet. 1e-5 ft ≈ 3 micrometres — generous enough
# to absorb JSON float serialisation noise and Revit's tessellation jitter while
# still catching any real geometric movement.
_GEOM_ABS_TOL = 1e-5
_GEOM_REL_TOL = 1e-6


def values_equal(a, b):
    """Exact equality for non-numerics; tolerant float comparison for numbers."""
    if a == b:
        return True
    try:
        return math.isclose(float(a), float(b), rel_tol=1e-6, abs_tol=1e-9)
    except (TypeError, ValueError):
        return False


def geom_floats_equal(a, b):
    """Float comparison tuned for Revit coordinate noise (feet, internal units)."""
    if a == b:
        return True
    try:
        return math.isclose(float(a), float(b), rel_tol=_GEOM_REL_TOL, abs_tol=_GEOM_ABS_TOL)
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
    """Recursively compare coordinate arrays with geometry-appropriate float tolerance."""
    if a is None and b is None:
        return True
    if a is None or b is None:
        return False
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            return False
        return all(coords_equal(x, y) for x, y in zip(a, b))
    return geom_floats_equal(a, b)


def geometries_equal(a, b):
    if a is None and b is None:
        return True
    if a is None or b is None:
        return False
    if a.get("type") != b.get("type"):
        return False
    if a.get("type") == "RevitTransform":
        return all(
            coords_equal(a.get(f), b.get(f))
            for f in ("origin", "basis_x", "basis_y", "basis_z")
        )
    return coords_equal(a.get("coordinates"), b.get("coordinates"))


def geometry_summary(geom):
    """Short human-readable description of a geometry for console output."""
    if geom is None:
        return "None"
    gtype = geom.get("type")
    coords = geom.get("coordinates")
    if gtype == "Point" and coords:
        return f"Point({coords[0]:.3f}, {coords[1]:.3f}, {coords[2]:.3f})"
    if gtype == "RevitTransform":
        o = geom.get("origin") or []
        bz = geom.get("basis_z") or []
        origin_str = f"({o[0]:.3f}, {o[1]:.3f}, {o[2]:.3f})" if len(o) == 3 else "?"
        normal_str = f"({bz[0]:.3f}, {bz[1]:.3f}, {bz[2]:.3f})" if len(bz) == 3 else "?"
        return f"RevitTransform(origin={origin_str}, normal={normal_str})"
    if gtype == "LineString" and coords:
        return f"LineString({len(coords)} pts)"
    if gtype == "Polygon" and coords:
        total_pts = sum(len(ring) for ring in coords)
        return f"Polygon({len(coords)} ring(s), {total_pts} pts)"
    return str(geom)


def _linestring_length(coords):
    total = 0.0
    for i in range(len(coords) - 1):
        p1, p2 = coords[i], coords[i + 1]
        n = min(len(p1), len(p2))
        total += math.sqrt(sum((p2[k] - p1[k]) ** 2 for k in range(n)))
    return total


def _rotation_angle_deg(old_g, new_g):
    try:
        bx_o = old_g.get("basis_x", [])
        by_o = old_g.get("basis_y", [])
        bz_o = old_g.get("basis_z", [])
        bx_n = new_g.get("basis_x", [])
        by_n = new_g.get("basis_y", [])
        bz_n = new_g.get("basis_z", [])
        if not all(len(v) == 3 for v in [bx_o, by_o, bz_o, bx_n, by_n, bz_n]):
            return None
        # trace(R_new @ R_old^T) = dot(bx_new, bx_old) + dot(by_new, by_old) + dot(bz_new, bz_old)
        trace = (
            sum(bx_n[i] * bx_o[i] for i in range(3)) +
            sum(by_n[i] * by_o[i] for i in range(3)) +
            sum(bz_n[i] * bz_o[i] for i in range(3))
        )
        return math.degrees(math.acos(max(-1.0, min(1.0, (trace - 1.0) / 2.0))))
    except Exception:
        return None


def _geometry_delta(old_g, new_g):
    if old_g is None and new_g is not None:
        return "added"
    if old_g is not None and new_g is None:
        return "removed"
    if old_g is None and new_g is None:
        return ""
    if old_g.get("type") != new_g.get("type"):
        return f"type: {old_g.get('type')} → {new_g.get('type')}"

    gtype = old_g.get("type")

    if gtype == "RevitTransform":
        parts = []
        oo = old_g.get("origin") or []
        no = new_g.get("origin") or []
        if len(oo) == 3 and len(no) == 3:
            dist = math.sqrt(sum((b - a) ** 2 for a, b in zip(oo, no)))
            parts.append(f"Δpos={dist:.4f} ft")
        angle = _rotation_angle_deg(old_g, new_g)
        if angle is not None:
            parts.append(f"Δrot={angle:.2f}°")
        return ", ".join(parts) if parts else "changed"

    if gtype == "LineString":
        oc = old_g.get("coordinates") or []
        nc = new_g.get("coordinates") or []
        if oc and nc:
            return f"Δlen={_linestring_length(nc) - _linestring_length(oc):+.4f} ft"

    if gtype == "Polygon":
        oc = old_g.get("coordinates") or []
        nc = new_g.get("coordinates") or []
        if oc and nc:
            old_p = sum(_linestring_length(ring) for ring in oc)
            new_p = sum(_linestring_length(ring) for ring in nc)
            return f"Δperimeter={new_p - old_p:+.4f} ft"

    return "changed"


def compute_delta(change):
    pid_type = change.get("param_id_type")
    val_type = change.get("value_type") or ""
    ov = change.get("old_value")
    nv = change.get("new_value")

    if pid_type == "geometry":
        return _geometry_delta(ov, nv)

    if ov is None and nv is not None:
        return "added"
    if ov is not None and nv is None:
        return "removed"
    if ov is None and nv is None:
        return ""

    if val_type == "ElementId":
        return "ref changed"

    try:
        return f"{float(nv) - float(ov):+g}"
    except (TypeError, ValueError):
        pass

    if isinstance(ov, str) and isinstance(nv, str):
        if len(ov) + len(nv) <= 60:
            return f"'{ov}' → '{nv}'"
        return f"{len(ov)} → {len(nv)} chars"

    return "changed"


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

        for c in changes:
            c["delta"] = compute_delta(c)

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
                    "delta":         c.get("delta"),
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
    parser = argparse.ArgumentParser(
        description="Compare two Perseus full-sync JSON exports and report element changes.",
        epilog=(
            "Changes are reported as added, removed, or modified. "
            "Geometry changes appear alongside parameter changes in the modified list, "
            "distinguished by param_id_type='geometry'. "
            "Best used with two full-sync exports — absent elements in incremental exports "
            "mean 'unchanged', not 'deleted'."
        ),
    )
    parser.add_argument("file_a", help="Older export JSON")
    parser.add_argument("file_b", help="Newer export JSON")
    parser.add_argument("output", nargs="?", default=None, help="Output JSON report path (optional)")
    args = parser.parse_args()
    report(args.file_a, args.file_b, args.output)
