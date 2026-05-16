Project Perseus - Architecture & Context Guide
1. Project Overview

Project Perseus is an enterprise-grade Revit plugin (.NET C#) developed for architecture firms. It acts as an automated data pipeline that extracts element data and geometry states from Revit models and pushes them to an external REST backend (which is currently Django, but is agnostic for different users).

The primary trigger for this pipeline is the standard Revit "Sync with Central" action. Perseus intercepts this event, calculates what has changed in the model, queues the user if necessary, and offloads the data via HTTP JSON payloads.
2. Tech Stack & Environment

    Environment: Revit API (targets 2024, 2025, 2026).

    Language: C# (.NET Framework 4.8+).

    Backend: Django Server (JSON APIs).

    Authentication: Microsoft Authentication Library (MSAL) for Entra ID, dynamically configured by the backend.

    Deployment: Multi-version compiled installer using Inno Setup (Pascal scripting) for zero-touch IT deployment.

3. Core Modules & Execution Flow
A. The Sync Intercept (Plugin.cs)

The plugin hooks into DocumentSynchronizingWithCentral.

    Pre-Sync: Checks the Django server queue. If other users are syncing, it blocks the sync, prompts the user via a WPF/WinForms UI, and optionally spins up the QueuePoller to wait for their turn.

    Post-Sync: Hooked into DocumentSynchronizedWithCentral (and a ProgressChanged fallback for early release). Determines if the model requires a Full Sync or Incremental Sync.

B. Incremental vs. Full Sync Logic

    Full Sync: (PerformFullSync) Iterates through all elements, wraps them in a custom ElementDelta class, and pushes the entire state.

    Incremental Sync: (PerformIncrementalSync) Relies on Revit's GetElementChangeSet and the local PacCache to identify only newly created, modified, or deleted elements since the last known GUID.

    The PacCache Fallback: If a user's local cache is missing (throws baseVersionGUID exception), the catch block gracefully catches it and forces a PerformFullSync to re-establish the baseline.

C. Authentication Engine (AuthService.cs)

Uses Server-Driven Configuration. The plugin makes an unauthenticated GET request to the Django server to ask how it should authenticate.

    Enterprise Mode: Django returns a Microsoft Tenant ID and Client ID. The plugin uses MSAL to pop an Entra ID login, silently caches the token using Windows Credential Manager, and injects it as a Bearer token into HttpClient.

    Sandbox Mode: If Django returns "authType": "None", the plugin skips MSAL entirely and passes a dummy token, allowing for unauthenticated testing by external partners or university students.

D. Concurrency & Queueing (QueuePoller.cs)

Prevents database collisions when multiple architects sync the same central model simultaneously.

    Runs on a background Task.

    Polls a Django endpoint every 5 seconds.

    When the current user's Windows Username is first in the returned JSON array, the poller uses a Revit ExternalEvent to safely wake up the main Revit UI thread and trigger the sync automatically.

E. Batch / Headless Processing

Perseus supports a zero-touch batch mode.

    On IExternalApplication.OnStartup, it checks %AppData% for a batch_task.json file.

    If found, it hooks into Revit's Idling event, waits 3 seconds for the UI to settle, and launches BatchProcessor to automate model processing without human interaction.

F. Element Tracking (ModelGuidStorage.cs)

Since modifying elements directly causes Revit worksharing conflicts, Perseus tags the Document itself with Extensible Storage (a hidden DataStorage element) containing a unique database GUID. All individual element variables are stored externally in the Django database keyed to the element's immutable UniqueId.
4. UI & Resources

    Ribbon buttons adaptively switch between Light and Dark mode using WPF pack:// URIs pointing to Resource build-action .png files.

    User settings (Server URL) are saved locally to config.json in the user's AppData folder.