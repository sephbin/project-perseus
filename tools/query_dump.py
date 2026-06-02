"""
query_dump.py
Interactive query helper for Perseus full-sync dumps written by FullSyncRunner
to %AppData%\\ProjectPerseus\\fullsync-dumps\\<timestamp>-<docname>-fullsync.json.

Loads the dump once and drops you into a Python REPL with helpers ready, so
every subsequent search is instant (the slow part is the initial json.loads).

Usage:
    python -i query_dump.py <dump.json>

At the >>> prompt:
    summarize()                                    # quick overview + top categories
    find_by_name("Wall")[:5]                       # name regex (case-insensitive)
    find_by_uid("abc-123-…")                       # exact unique_id
    find_by_id(347291)                             # exact element_id
    with_param("Is FamilySymbol", True)            # filter by parameter
    with_param("Category", "-2000160")             # e.g. all rooms
    params(find_by_uid("abc-123-…")[0])            # dict of name->value
    deleted                                        # list of ghost element_ids

If a dump ever grows past ~500 MB and json.loads starts swapping, swap to ijson
(pure-Python wheel, `pip install ijson`) and stream elements one at a time.
"""

import json
import re
import sys
from collections import Counter
from pathlib import Path

path = Path(sys.argv[1]) if len(sys.argv) > 1 else None
if not path or not path.exists():
    raise SystemExit("Usage: python -i query_dump.py <dump.json>")

print(f"Loading {path.name} ({path.stat().st_size / 1_048_576:.1f} MB)...")
data = json.loads(path.read_text(encoding="utf-8"))
elements = data.get("elements", [])
deleted = data.get("deletedElements", [])
print(f"Loaded {len(elements)} elements, {len(deleted)} deletions.")


def els():
    """Inner element dicts (strips the outer {action, element} wrapper)."""
    return [d["element"] for d in elements if "element" in d]


def find_by_name(pattern, ci=True):
    rx = re.compile(pattern, re.IGNORECASE if ci else 0)
    return [e for e in els() if rx.search(e.get("name") or "")]


def find_by_uid(uid):
    return [e for e in els() if e.get("unique_id") == uid]


def find_by_id(eid):
    return [e for e in els() if str(e.get("element_id")) == str(eid)]


def params(elem):
    return {p["name"]: p.get("value") for p in elem.get("parameters", [])}


def with_param(name, value=None):
    out = []
    for e in els():
        for p in e.get("parameters", []):
            if p.get("name") != name:
                continue
            if value is None or p.get("value") == value:
                out.append(e)
                break
    return out


def summarize():
    print(f"Elements:     {len(els())}")
    print(f"Deletions:    {len(deleted)}")
    print(f"Source GUID:  {data.get('documentGuid')}")
    print(f"State:        {data.get('source_state')}")
    print(f"User/Machine: {data.get('windowsUser')} / {data.get('machine')}")
    print(f"Timestamp:    {data.get('timestamp')}")
    cats = Counter()
    for e in els():
        for p in e.get("parameters", []):
            if p.get("name") == "Category":
                cats[p.get("value")] += 1
                break
    print("Top categories (by element count):")
    for cat, n in cats.most_common(10):
        print(f"  {n:>6}  {cat}")
