"""
anonymize.py
Anonymises last_edited_by usernames in a Perseus JSON export.

Each unique username is replaced with a stable UUID token.
The mapping is persisted in a user key file so subsequent runs produce the same
tokens for the same users.  The key file can be enriched by hand with
display_name, email, and notes fields for future reference or de-anonymisation.

The random seed used to generate UUIDs is stored in the key file under "seed".
A fresh seed is generated automatically when the key file is first created.

Usage:
    python anonymize.py <export.json> [output.json] [--key user_key.json] [--deep]

    --key   Path to the user key file (created if absent).
            Defaults to user_key.json in the same directory as the input.
    --deep  Also replace username occurrences inside string parameter values.
            Off by default — only last_edited_by is replaced unless this flag
            is given.
"""

import argparse
import json
import random
import re
import uuid
from datetime import datetime, timezone
from pathlib import Path


# ---------------------------------------------------------------------------
# Key file helpers
# ---------------------------------------------------------------------------

def load_key(key_path: Path) -> dict:
    """Load the user key file; create a fresh structure with a new seed if absent."""
    if key_path.exists():
        with open(key_path, "r", encoding="utf-8") as f:
            return json.load(f)
    return {
        "generated_at": _now(),
        "last_updated": _now(),
        "seed": random.getrandbits(32),
        "users": [],
    }


def save_key(key_path: Path, key_data: dict) -> None:
    key_data["last_updated"] = _now()
    with open(key_path, "w", encoding="utf-8") as f:
        json.dump(key_data, f, indent=2)


def build_lookup(key_data: dict) -> dict[str, str]:
    """Return {username: anon_id} from the key data."""
    return {u["username"]: u["anon_id"] for u in key_data["users"]}


def _make_rng(key_data: dict, advance: int) -> random.Random:
    """Create a seeded RNG advanced past `advance` already-issued UUIDs."""
    rng = random.Random(key_data["seed"])
    for _ in range(advance):
        rng.getrandbits(128)
    return rng


def next_anon_id(rng: random.Random) -> str:
    return str(uuid.UUID(int=rng.getrandbits(128), version=4))


def get_or_create_token(username: str, key_data: dict, lookup: dict, rng: random.Random) -> str:
    """Return existing token for username, or create and register a new one."""
    if username in lookup:
        return lookup[username]
    anon_id = next_anon_id(rng)
    key_data["users"].append({
        "anon_id": anon_id,
        "username": username,
        "display_name": None,
        "email": None,
        "notes": None,
    })
    lookup[username] = anon_id
    return anon_id


# ---------------------------------------------------------------------------
# Anonymisation
# ---------------------------------------------------------------------------

def anonymise_export(data, key_data: dict, rng: random.Random, deep: bool):
    """Walk the export structure, anonymise every element, return the modified copy."""
    lookup = build_lookup(key_data)

    if isinstance(data, list):
        return [_anonymise_delta(d, key_data, lookup, rng, deep) for d in data]

    result = {k: v for k, v in data.items() if k != "elements"}
    result["elements"] = [
        _anonymise_delta(d, key_data, lookup, rng, deep)
        for d in data.get("elements", [])
    ]
    return result


def _anonymise_delta(delta: dict, key_data: dict, lookup: dict, rng: random.Random, deep: bool) -> dict:
    delta = dict(delta)
    if "element" in delta and isinstance(delta["element"], dict):
        delta["element"] = _anonymise_element(delta["element"], key_data, lookup, rng, deep)
    return delta


def _anonymise_element(element: dict, key_data: dict, lookup: dict, rng: random.Random, deep: bool) -> dict:
    elem = dict(element)

    raw = elem.get("last_edited_by")
    if raw and isinstance(raw, str) and raw.strip():
        elem["last_edited_by"] = get_or_create_token(raw.strip(), key_data, lookup, rng)

    if deep and "parameters" in elem:
        elem["parameters"] = _anonymise_params(elem["parameters"], lookup)

    return elem


def _anonymise_params(params: list, lookup: dict) -> list:
    """Replace any known username that appears verbatim in a string parameter value."""
    result = []
    for p in params:
        p = dict(p)
        val = p.get("value")
        if isinstance(val, str):
            p["value"] = _replace_all_known(val, lookup)
        result.append(p)
    return result


def _replace_all_known(text: str, lookup: dict) -> str:
    """Word-boundary replacement of all known usernames in text."""
    for username, anon_id in lookup.items():
        text = re.sub(r"\b" + re.escape(username) + r"\b", anon_id, text)
    return text


# ---------------------------------------------------------------------------
# Utility
# ---------------------------------------------------------------------------

def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _default_output(input_path: Path) -> Path:
    return input_path.with_name(input_path.stem + "_anon" + input_path.suffix)


def _default_key(input_path: Path) -> Path:
    return input_path.parent / "user_key.json"


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def run(input_path: str, output_path: str | None, key_path: str | None, deep: bool) -> None:
    inp = Path(input_path)
    out = Path(output_path) if output_path else _default_output(inp)
    key = Path(key_path) if key_path else _default_key(inp)

    with open(inp, "r", encoding="utf-8") as f:
        data = json.load(f)

    key_data = load_key(key)
    users_before = len(key_data["users"])

    rng = _make_rng(key_data, advance=users_before)
    anon_data = anonymise_export(data, key_data, rng, deep)

    users_after = len(key_data["users"])
    new_users = users_after - users_before

    with open(out, "w", encoding="utf-8") as f:
        json.dump(anon_data, f, indent=2)

    save_key(key, key_data)

    elements = anon_data if isinstance(anon_data, list) else anon_data.get("elements", [])
    print(f"\n{'='*55}")
    print(f"  Anonymised {len(elements)} element delta(s)")
    print(f"  Total users in key : {users_after}  ({new_users} new this run)")
    print(f"  Output             : {out}")
    print(f"  Key file           : {key}")
    print(f"{'='*55}")
    if new_users:
        print(f"\n  New entries added to key file:")
        for u in key_data["users"][users_before:]:
            print(f"    {u['anon_id']}  ←  {u['username']}")
        print(f"\n  Edit {key.name} to add display_name / email for each user.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="Anonymise last_edited_by usernames in a Perseus JSON export.",
        epilog=(
            "A user key file is created (or updated) alongside the output. "
            "The same username always maps to the same token across runs. "
            "Enrich the key file by hand with display_name and email for future "
            "de-anonymisation."
        ),
    )
    parser.add_argument("export", help="Input Perseus export JSON")
    parser.add_argument("output", nargs="?", default=None, help="Output JSON path (default: <export>_anon.json)")
    parser.add_argument("--key", default=None, help="User key file path (default: user_key.json alongside export)")
    parser.add_argument("--deep", action="store_true", help="Also replace usernames inside string parameter values")
    args = parser.parse_args()
    run(args.export, args.output, args.key, args.deep)
