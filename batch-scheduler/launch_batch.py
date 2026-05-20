"""
launch_batch.py  -  Perseus Batch Launcher (Python version)
Writes batch_task.json with a fresh timestamp, then opens Revit 2026.

Requires Python 3.6+ (stdlib only, no third-party packages).
Schedule with install_task.bat, or run manually to test.
"""

import json
import os
import subprocess
import sys
from datetime import datetime

# ------------------------------------------------------------------
# CONFIGURATION
# ------------------------------------------------------------------

REVIT_EXE = r"C:\Program Files\Autodesk\Revit 2026\Revit.exe"

# Edit this list to match the models you want to process.
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
    """Return True if a Revit process is already running."""
    result = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Revit.exe", "/NH"],
        capture_output=True, text=True
    )
    return "Revit.exe" in result.stdout


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

    # Timestamp must be within 4 hours of Revit startup (enforced by the plugin).
    instruction = {
        "models_to_process": MODELS,
        "timestamp": datetime.now().strftime("%Y-%m-%dT%H:%M:%S"),
    }

    with open(batch_file, "w", encoding="utf-8") as f:
        json.dump(instruction, f, indent=2)

    print(f"Written: {batch_file}")
    print("Launching Revit 2026...")

    subprocess.Popen([REVIT_EXE])


if __name__ == "__main__":
    main()
