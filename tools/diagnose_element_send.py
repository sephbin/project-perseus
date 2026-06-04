"""
diagnose_element_send.py
Sends a single diagnosed element JSON to the Django-Perseus server and reports
the HTTP response. Used to reproduce and diagnose ingestion failures without
running a full sync from Revit.

Input sources (pick one):
  --from-log LOG_FILE --element-id ID   Extract element JSON from a Perseus log file
  --json-file FILE                       Read element JSON from a file
  --json-str  '{"action":...}'           Pass element JSON inline

The element JSON is the block logged by DiagnoseElementCommand:
  "DiagnoseElement [ID]: element payload JSON: { ... }"

Auth is auto-detected by fetching /api/auth-config/ from the server:
  None    → no Authorization header required
  JWT     → POST /api/token/ with --username / --password (prompted if omitted)
  EntraID → requires --token (MSAL interactive auth not supported in CLI tools)
  Any mode → --token always overrides auto-detection

Stdlib only — no pip installs required.

Usage examples:
  python tools/diagnose_element_send.py \\
      --url https://labs.cox.com.au/perseus \\
      --from-log "%APPDATA%/ProjectPerseus/logs/20260604-123456-Andrew.Butler-medusa.log" \\
      --element-id 6640047

  python tools/diagnose_element_send.py \\
      --url https://labs.cox.com.au/perseus \\
      --json-file element.json \\
      --token MY_PAT_TOKEN \\
      --endpoint crud
"""

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from getpass import getpass


# ── HTTP helpers ──────────────────────────────────────────────────────────────

def _http_post(url, payload, token=None):
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")
    except Exception as e:
        return None, str(e)


def _http_get(url):
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except Exception as e:
        print(f"  [warn] GET {url} failed: {e}")
        return {}


# ── auth ──────────────────────────────────────────────────────────────────────

def resolve_token(base_url, token_override, username, password):
    if token_override:
        print("[auth] using --token override")
        return token_override

    cfg = _http_get(f"{base_url}/api/auth-config/")
    auth_type = cfg.get("authType", "None")
    print(f"[auth] server authType = {auth_type!r}")

    if auth_type == "None":
        return None

    if auth_type == "JWT":
        endpoint = cfg.get("tokenEndpoint") or f"{base_url}/api/token/"
        if not username:
            username = input("  Username: ").strip()
        if not password:
            password = getpass("  Password: ")
        status, body = _http_post(endpoint, {"username": username, "password": password})
        if status != 200:
            print(f"  [error] JWT login returned HTTP {status}: {body[:300]}")
            sys.exit(1)
        data = json.loads(body)
        tok = data.get("access") or data.get("token") or data.get("access_token")
        if not tok:
            print(f"  [error] JWT response had no access token: {data}")
            sys.exit(1)
        print("  [auth] JWT token acquired")
        return tok

    if auth_type == "EntraID":
        print("  [error] EntraID requires --token (pass a Bearer token or PAT manually)")
        sys.exit(1)

    print(f"  [warn] unknown authType {auth_type!r} — proceeding without token")
    return None


# ── log extraction ────────────────────────────────────────────────────────────

def load_from_log(log_path, element_id):
    """
    Scan a Perseus log file for the LAST occurrence of
    'DiagnoseElement [ID]: element payload JSON:' and return the JSON block
    that follows it.
    """
    marker = f"DiagnoseElement [{element_id}]: element payload JSON:"
    text = open(log_path, encoding="utf-8", errors="replace").read()

    # Find the last occurrence so repeated Diagnose runs give the freshest result.
    pos = text.rfind(marker)
    if pos == -1:
        print(f"[error] marker not found in log:\n  {marker}")
        print(f"  Ensure you ran 'Diagnose Elements' with id={element_id} and that")
        print(f"  verbose logging is not required for DiagnoseElement output (it isn't).")
        sys.exit(1)

    # Advance past the marker line's newline to reach the start of the JSON block.
    start = text.find("\n", pos)
    if start == -1:
        print("[error] unexpected end of file after marker")
        sys.exit(1)
    start += 1

    # The next line that starts with a timestamp ends the JSON block.
    ts_re = re.compile(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", re.MULTILINE)
    m = ts_re.search(text, start)
    end = m.start() if m else len(text)

    raw = text[start:end].strip()
    if not raw:
        print("[error] empty content after marker — was the payload JSON actually logged?")
        sys.exit(1)

    try:
        return json.loads(raw)
    except json.JSONDecodeError as e:
        print(f"[error] JSON parse failed: {e}")
        print(f"  first 500 chars of raw block:\n  {raw[:500]}")
        sys.exit(1)


def load_element_delta(args):
    if args.from_log:
        if not args.element_id:
            print("[error] --from-log requires --element-id")
            sys.exit(1)
        return load_from_log(args.from_log, args.element_id)
    if args.json_file:
        try:
            return json.loads(open(args.json_file, encoding="utf-8").read())
        except Exception as e:
            print(f"[error] reading {args.json_file}: {e}")
            sys.exit(1)
    if args.json_str:
        try:
            return json.loads(args.json_str)
        except json.JSONDecodeError as e:
            print(f"[error] parsing --json-str: {e}")
            sys.exit(1)
    print("[error] supply one of: --from-log, --json-file, --json-str")
    sys.exit(1)


# ── payload validation ────────────────────────────────────────────────────────

def validate(delta):
    """
    Check the ElementDelta for fields that commonly cause silent ingestion
    failures in process_crud_queue_fast.
    """
    issues = []
    warnings = []

    elem = delta.get("element")
    if not elem:
        issues.append("Missing 'element' key — the delta is malformed")
        return issues, warnings

    for field in ("unique_id", "source_model", "element_id"):
        if not elem.get(field):
            issues.append(f"Missing or empty required field: element.{field}")

    if not elem.get("source_model", "").strip():
        issues.append("element.source_model is blank — Django will 404 on Source lookup")

    params = elem.get("parameters") or []
    if not params:
        warnings.append("element.parameters is empty — element will be stored with no parameters")

    # Duplicate parameter names (Django deduplicates, but worth flagging)
    name_counts: dict = {}
    for p in params:
        n = p.get("name")
        name_counts[n] = name_counts.get(n, 0) + 1
    dups = {k: v for k, v in name_counts.items() if v > 1}
    if dups:
        warnings.append(f"Duplicate parameter names (Django will keep one per name): {dups}")

    # Null values — Django maps these to '<none>' which is fine but worth knowing
    null_count = sum(1 for p in params if p.get("value") is None)
    if null_count:
        warnings.append(f"{null_count} parameter(s) have null value (Django maps to '<none>')")

    return issues, warnings


# ── send & report ─────────────────────────────────────────────────────────────

def send(url, payload, token, label):
    # Print equivalent curl for manual replication
    tok_header = f'-H "Authorization: Bearer {token}" ' if token else ""
    print(f"\n  # equivalent curl:")
    print(f"  curl -s -X POST \"{url}\" \\")
    print(f"    {tok_header}-H \"Content-Type: application/json\" \\")
    print(f"    -d @payload.json")

    print(f"\n→ POST {url}")
    status, body = _http_post(url, payload, token)
    if status is None:
        print(f"  ✗ connection error: {body}")
        return

    ok = "✓" if 200 <= status < 300 else "✗"
    print(f"  {ok} HTTP {status}")
    if body.strip():
        try:
            print(json.dumps(json.loads(body), indent=2))
        except json.JSONDecodeError:
            print(body[:1000])

    if status == 500:
        print("\n  [hint] HTTP 500 usually means an unhandled exception in the view.")
        print("         Check the Django server logs for the traceback.")
    elif status == 404:
        print("\n  [hint] HTTP 404 inside a background task often means the source_model")
        print("         GUID is not registered in Django's Source table.")
        print(f"         Expected GUID: {payload.get('documentGuid') or payload.get('elements', [{}])[0].get('element', {}).get('source_model')}")
        print(f"         Check Django admin → Sources for that GUID.")
    elif 200 <= status < 300:
        print("\n  [hint] 2xx means the task is QUEUED, not yet processed.")
        print("         Run 'python manage.py process_tasks' on the server,")
        print("         then check Django admin to confirm ingestion.")


# ── main ──────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(
        description="Send a single diagnosed element to Django-Perseus for ingestion testing",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--url",        required=True, metavar="URL",
                    help="Server base URL  e.g. https://labs.cox.com.au/perseus")
    ap.add_argument("--from-log",   metavar="LOG",
                    help="Perseus log file to extract element JSON from")
    ap.add_argument("--element-id", metavar="ID",
                    help="Element ID to extract (used with --from-log)")
    ap.add_argument("--json-file",  metavar="FILE",
                    help="JSON file containing an ElementDelta")
    ap.add_argument("--json-str",   metavar="JSON",
                    help="ElementDelta JSON string (inline)")
    ap.add_argument("--token",      metavar="TOK",
                    help="Bearer token / PAT — overrides auth-config detection")
    ap.add_argument("--username",   metavar="USER",
                    help="Username for JWT auth")
    ap.add_argument("--password",   metavar="PASS",
                    help="Password for JWT auth (prompted if omitted)")
    ap.add_argument("--endpoint",   choices=["crud", "state", "both"], default="both",
                    help="Endpoint(s) to send to (default: both)")
    ap.add_argument("--dry-run",    action="store_true",
                    help="Validate and print the payload without sending")
    args = ap.parse_args()

    base = args.url.rstrip("/")

    # ── load ──────────────────────────────────────────────────────────────────
    delta = load_element_delta(args)
    elem  = delta.get("element", {})

    print("=" * 64)
    print(f"  Element payload")
    print(f"  element_id   : {elem.get('element_id')}")
    print(f"  unique_id    : {elem.get('unique_id')}")
    print(f"  name         : {elem.get('name')}")
    print(f"  source_model : {elem.get('source_model')}")
    print(f"  source_state : {elem.get('source_state')}")
    print(f"  parameters   : {len(elem.get('parameters') or [])} entries")
    print("=" * 64)

    # ── validate ──────────────────────────────────────────────────────────────
    issues, warnings = validate(delta)
    if issues:
        print(f"\n[validation] {len(issues)} ERROR(S) — these will cause ingestion to fail:")
        for i in issues:
            print(f"  ✗ {i}")
    if warnings:
        print(f"\n[validation] {len(warnings)} WARNING(S):")
        for w in warnings:
            print(f"  ⚠ {w}")
    if not issues and not warnings:
        print("\n[validation] no issues found")

    if args.dry_run:
        print("\n[dry-run] full payload JSON:")
        print(json.dumps(delta, indent=2))
        return

    # ── auth ──────────────────────────────────────────────────────────────────
    token = resolve_token(base, args.token, args.username, args.password)

    # ── send ──────────────────────────────────────────────────────────────────
    source_model = elem.get("source_model", "")

    if args.endpoint in ("crud", "both"):
        print("\n── /add_to_crud_queue/ (bulk background path) " + "─" * 17)
        crud_payload = {
            "documentGuid": source_model,
            "elements":     [delta],
            "deletedElements": [],
        }
        send(f"{base}/add_to_crud_queue/", crud_payload, token, "add_to_crud_queue")

    if args.endpoint in ("state", "both"):
        print("\n── /stateupdate/ (sequential background path) " + "─" * 17)
        state_payload = {"elements": [delta]}
        send(f"{base}/stateupdate/", state_payload, token, "stateUpdate")

    print()


if __name__ == "__main__":
    main()
