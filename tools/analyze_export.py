"""
analyze_export.py
Analyzes a Perseus full/incremental JSON export.
Outputs unique key paths, leaf key names, and parameter name→value_type mappings.

Usage:
    python analyze_export.py <export.json> [output.json]
    python analyze_export.py <export.json>          # prints to console only
"""

import json
import sys
from collections import defaultdict
from datetime import datetime, timezone
import pandas as pd


def load(filepath):
    with open(filepath, "r", encoding="utf-8") as f:
        data = json.load(f)
    return data


def collect_keys(obj, prefix="", key_paths=None):
    if key_paths is None:
        key_paths = defaultdict(set)
    if isinstance(obj, dict):
        for k, v in obj.items():
            path = f"{prefix}.{k}" if prefix else k
            key_paths[path].add(type(v).__name__)
            collect_keys(v, path, key_paths)
    elif isinstance(obj, list):
        for item in obj:
            collect_keys(item, prefix, key_paths)
    return key_paths


def collect_parameters(data):
    seen = defaultdict(set)
    elements = data if isinstance(data, list) else data.get("elements", [])
    for delta in elements:
        element = delta.get("element", {})
        for param in element.get("parameters", []):
            name = param.get("name")
            vtype = param.get("value_type", "unknown")
            if name:
                seen[name].add(vtype)
    return seen


def analyze(filepath, output_path=None):
    data = load(filepath)
    elements = data if isinstance(data, list) else data.get("elements", [])
    meta_keys = {} if isinstance(data, list) else {k: v for k, v in data.items() if k != "elements"}

    key_paths = collect_keys(data)
    param_map = collect_parameters(data)

    key_rows = [
        {"key_path": path, "observed_types": ", ".join(sorted(types)), "depth": path.count(".") + 1}
        for path, types in key_paths.items()
    ]
    df_keys = pd.DataFrame(key_rows).sort_values(["depth", "key_path"]).reset_index(drop=True)

    param_rows = [
        {"parameter_name": name, "value_types": ", ".join(sorted(vtypes))}
        for name, vtypes in param_map.items()
    ]
    df_params = pd.DataFrame(param_rows).sort_values("parameter_name").reset_index(drop=True)

    print(f"\n{'='*60}")
    print(f"  {len(elements)} element delta(s) | {len(df_keys)} key paths | {len(df_params)} unique parameters")
    print(f"{'='*60}\n")
    print(df_keys.to_string(index=False))
    print(f"\n--- Unique parameter names ({len(df_params)}) ---")
    print(df_params.to_string(index=False))

    if output_path:
        report = {
            "meta": {
                "source_file": filepath,
                "generated_at": datetime.now(timezone.utc).isoformat(),
                "element_count": len(elements),
                "top_level_metadata_keys": list(meta_keys.keys()),
            },
            "key_paths": key_rows,
            "leaf_keys": sorted({p.split(".")[-1] for p in key_paths}),
            "parameters": param_rows,
        }
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(report, f, indent=2)
        print(f"\nJSON report written to: {output_path}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        src = input("Export JSON path: ").strip()
        out = input("Output JSON path (leave blank to skip): ").strip() or None
    else:
        src = sys.argv[1]
        out = sys.argv[2] if len(sys.argv) > 2 else None
    analyze(src, out)
