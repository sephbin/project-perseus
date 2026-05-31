"""
launch_batch.py  -  Perseus Batch Launcher (Python version)

Writes batch_task.json (or resumes an existing one) and loops launching
Revit until the plugin reports the batch is fully complete.

Survival loop: the plugin's BatchProcessor exits Revit with code 1 when
a cloud-model GetUserWorksetInfo poisons the session (and there's more
work pending), and code 0 when the batch is genuinely complete. This
script keeps relaunching Revit until batch_task.json is deleted by the
plugin (signaling completion) or the relaunch cap is hit.

Requires Python 3.6+ (stdlib only, no third-party packages).
Schedule with install_task.bat, or run manually to test.
"""

import json
import os
import subprocess
import sys
import time
from datetime import datetime

# ------------------------------------------------------------------
# CONFIGURATION
# ------------------------------------------------------------------

REVIT_EXE         = r"C:\Program Files\Autodesk\Revit 2026\Revit.exe"
MAX_RELAUNCHES    = 20
RELAUNCH_DELAY_S  = 5

# Edit this list to match the models you want to process.
# Only used when batch_task.json doesn't exist OR has zero pending models.
# A leftover file with pending work takes priority and this list is ignored
# for that session (so crash-survival state isn't clobbered).
MODELS = [
    {
        "model_guid":              "99f865fc-b79f-4d04-8105-40f15f3cbba6",
        "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
        "region":                  "AUS",
        "workset_blacklist_regex": ".",
        "workset_whitelist_regex": "^A-",
    },
    {
        "model_guid":              "ff8b5406-b4ff-4efb-86c0-3d93a36971a9",
        "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
        "region":                  "AUS",
        "workset_blacklist_regex": ".",
        "workset_whitelist_regex": "^A-",
    },
    {
        "model_guid":              "40061fc2-0d97-4c6a-a9c1-110319730f8c",
        "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
        "region":                  "AUS",
        "workset_blacklist_regex": ".",
        "workset_whitelist_regex": "^A-",
    },
    {
        "model_guid":              "08cff244-9181-46dc-91f6-27877f9e2e8b",
        "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
        "region":                  "AUS",
        "workset_blacklist_regex": ".",
        "workset_whitelist_regex": "^A-",
    },
    {
        "model_guid":              "c84fd25e-3db1-40ed-8834-3cdd29d7a3c8",
        "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
        "region":                  "AUS",
        "workset_blacklist_regex": ".",
        "workset_whitelist_regex": "^A-",
    },
]

# ------------------------------------------------------------------
# MAIN
# ------------------------------------------------------------------

def is_revit_running():
    result = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Revit.exe", "/NH"],
        capture_output=True, text=True
    )
    return "Revit.exe" in result.stdout


def has_pending_models(batch_file):
    """True if batch_task.json exists and lists at least one model."""
    if not os.path.exists(batch_file):
        return False
    try:
        with open(batch_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        return len(data.get("models_to_process", [])) > 0
    except Exception as e:
        print(f"Could not parse existing batch_task.json ({e}). Will overwrite.")
        return False


def write_fresh_batch(batch_file):
    instruction = {
        "models_to_process": MODELS,
        "timestamp": datetime.now().strftime("%Y-%m-%dT%H:%M:%S"),
    }
    with open(batch_file, "w", encoding="utf-8") as f:
        json.dump(instruction, f, indent=2)
    print(f"Fresh batch_task.json written: {batch_file} ({len(MODELS)} model(s)).")


def main():
    if is_revit_running():
        print("Revit is already running. Aborting to avoid conflicts.")
        sys.exit(1)

    if not os.path.exists(REVIT_EXE):
        print(f"Revit 2026 not found at: {REVIT_EXE}")
        sys.exit(1)

    batch_dir  = os.path.join(os.environ["APPDATA"], "ProjectPerseus")
    batch_file = os.path.join(batch_dir, "batch_task.json")
    os.makedirs(batch_dir, exist_ok=True)

    # Resume vs. fresh
    if has_pending_models(batch_file):
        with open(batch_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        pending = len(data["models_to_process"])
        print(f"Resuming existing batch_task.json - {pending} model(s) pending.")
    else:
        write_fresh_batch(batch_file)

    # Survival loop: launch Revit, wait, decide based on file presence.
    for relaunch in range(1, MAX_RELAUNCHES + 1):
        print()
        print(f"=== Revit launch {relaunch} of {MAX_RELAUNCHES} ===")
        print("Starting Revit and waiting for exit...")

        proc = subprocess.Popen([REVIT_EXE])
        exit_code = proc.wait()
        print(f"Revit exited with code {exit_code}.")

        # Plugin signals "all done" by deleting batch_task.json. Trust the file's
        # presence over the code since a hard native crash might not set a clean
        # exit code at all.
        if not os.path.exists(batch_file):
            print("batch_task.json is gone. Batch complete.")
            sys.exit(0)

        if exit_code == 0:
            print("Exit 0 but batch_task.json still exists. Treating as complete and stopping to avoid loop.")
            sys.exit(0)

        print(f"More work pending. Relaunching in {RELAUNCH_DELAY_S} second(s)...")
        time.sleep(RELAUNCH_DELAY_S)

    print(f"Hit max relaunches ({MAX_RELAUNCHES}). Stopping launcher; investigate batch_task.json manually.")
    sys.exit(1)


if __name__ == "__main__":
    main()
