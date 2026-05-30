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

    Icon files in ui/icons/ use .png extensions but are actually ICO format containers with multiple embedded resolutions (16x16, 32x32). Do not replace them with true PNG files.

5. Build → Commit Workflow

The post-build pipeline runs on every build (Debug and Release): Build → BumpVersion → BuildInstaller → GitCommit.

GitCommit calls build-commit.ps1 which reads .claude_changes.md from the repo root, uses its
contents as the commit body, commits all staged changes, pushes, then clears the file.

IMPORTANT — Claude must maintain .claude_changes.md during every session:
- Append a concise bullet point for every meaningful change made to any file in this repo.
- Use the format:  - filename.cs: one-line description of what changed and why
- Do NOT clear the file manually — build-commit.ps1 clears it after a successful Release build.
- If the file is empty at build time the commit message will just say "Manual build".

6. File Structure & Code Organization (revit_plugin/src/ProjectPerseus/)

This section is the canonical map of where plugin code lives. When folders, files, or
namespaces are moved/added/renamed, update 6.1 (Current Layout) AND 6.3 (Target Layout) in
the same change. Claude relies on this section to place new code without re-deriving the
structure each session.

6.1 Current Layout (snapshot 2026-05-30)

ProjectPerseus/
├── Plugin.cs                     1139 lines — god class: IExternalApplication lifecycle,
│                                 sync event handlers, doOnPriorToSync, doOnPostSync,
│                                 PerformFullSync/IncrementalSync (+ ToFile variants),
│                                 GetAllCategories, OnProgressChanged, OnRevitIdlingDelay,
│                                 TryCloseRevitSyncDialog Win32 P/Invoke. Split target.
├── Commands.cs                   3 IExternalCommand classes in ProjectPerseus.Commands
│                                 namespace (only file using PascalCase namespace).
├── AuthService.cs                434 lines — MSAL + JWT + PAT + token cache + HTTP config.
├── ProjectPerseusWeb.cs          221 lines — HTTP client for Django; duplicates WebHelper.
├── ModelGuidStorage.cs           In ProjectPerseus.revit namespace despite root location.
├── Config.cs                     AppData config.json singleton.
├── Log.cs                        Static logger → Sentry + console.
├── Utl.cs                        Kitchen sink: WriteLog (file), JsonDump, SerializeToJson,
│                                 IsValidUrl, nested WebHelper, nested SentryContext.
├── auth/                         (empty — AuthService.cs lives at root despite namespace)
├── batch/
│   └── BatchProcessor.cs         In ProjectPerseus.queue namespace (folder/ns mismatch).
├── commands/                     Misnamed — contains queue infra, not IExternalCommands.
│   ├── AutoSyncEvent.cs          ProjectPerseus.queue namespace.
│   └── QueuePoller.cs            ProjectPerseus.queue namespace.
├── forms/                        WinForms #1: settings, sync warning, login, queue, etc.
├── ui/                           WinForms #2: BatchProgressForm, ThemeIconManager.
├── models/                       DTOs + geometry extractor.
│   └── geometry/                 (see feedback_csproj_include.md for ARDB alias rule)
├── revit/                        Revit API adapter layer (RevitFacade, extractors).
│   ├── adapters/                 Ardb* wrappers around Autodesk.Revit.DB types.
│   ├── interfaces/               IArdb* interfaces.
│   ├── plugin/                   2 more IExternalCommand classes (3rd command location).
│   └── BatchFailureHandler.cs    12 lines — belongs with batch/.
├── Properties/                   AssemblyInfo + designer files (do not touch).
└── resources/                    Icons (.png that are actually ICO containers — §4 rule).

6.2 Known Organization Smells (ranked by impact)

S1  Plugin.cs is 1139 lines mixing 7 distinct responsibilities — biggest dev-QoL win to split.
S2  IExternalCommand classes live in THREE places: Commands.cs (root), revit/plugin/. The
    commands/ folder confusingly holds queue infrastructure instead of commands.
S3  Two folders for WinForms (forms/ and ui/) with no rule for which goes where.
S4  Two parallel logging systems: Utl.WriteLog (file) vs Log (Sentry). Pick one front door.
S5  Utl.cs is a kitchen sink (logging, JSON, URL, WebHelper, SentryContext) — hard to find
    anything; WebHelper is duplicated verbatim in ProjectPerseusWeb.cs.
S6  AuthService.cs (434 lines) bundles MSAL, JWT, PAT, refresh-token persistence, token cache.
S7  Namespace ↔ folder mismatches: batch/ → ProjectPerseus.queue, commands/ → .queue,
    ModelGuidStorage.cs (root) → .revit, AuthService.cs (root) → .auth.
S8  Namespace casing inconsistency: ProjectPerseus.Commands (Pascal) vs everything else
    (lowercase). Lowercase is the de-facto norm — align Commands to commands.
S9  Six files at root (Config, Log, Utl, Commands, AuthService, ProjectPerseusWeb,
    ModelGuidStorage) all have a clear folder they should live in.

6.3 Target Layout (refactor plan — NOT yet executed)

ProjectPerseus/
├── Plugin.cs                     ~150 lines — only IExternalApplication lifecycle wiring.
├── auth/
│   ├── AuthService.cs            Coordinator: server config → pick MSAL/JWT/None.
│   ├── MsalAuth.cs               Entra ID interactive + silent flows.
│   ├── JwtAuth.cs                JWT login form + refresh.
│   ├── PersonalAccessToken.cs    PAT storage / clear.
│   └── TokenCache.cs             Refresh-token persistence to disk.
├── batch/
│   ├── BatchProcessor.cs         (namespace fixed to ProjectPerseus.batch)
│   └── BatchFailureHandler.cs    Moved from revit/.
├── commands/                     ONLY IExternalCommand classes.
│   ├── PerformFullUploadCommand.cs    Split from Commands.cs.
│   ├── OpenSettingsCommand.cs         Split from Commands.cs.
│   ├── ResetModelGuidCommand.cs       Split from Commands.cs.
│   ├── EditSettingsCommand.cs         Moved from revit/plugin/.
│   └── InitialiseProjectCommand.cs    Moved from revit/plugin/.
├── config/
│   └── Config.cs                 Moved from root.
├── logging/
│   ├── Log.cs                    Single front door — wraps Sentry + file output.
│   └── SentryContext.cs          Extracted from Utl.cs.
├── models/                       Mostly unchanged (existing folder is healthy).
├── queue/                        Renamed from commands/ (folder now matches namespace).
│   ├── AutoSyncEvent.cs
│   └── QueuePoller.cs
├── revit/                        Revit API adapter layer.
│   ├── ModelGuidStorage.cs       Moved from root (namespace already matches).
│   └── (rest unchanged)
├── sync/                         NEW — extracted from Plugin.cs.
│   ├── SyncOrchestrator.cs       OnDocumentSynchronizing/zed event handlers.
│   ├── PreSyncQueue.cs           doOnPriorToSync + OpenWebQueueLink.
│   ├── PostSyncDispatcher.cs     doOnPostSync + doOnSync (full vs incremental routing).
│   ├── FullSyncRunner.cs         PerformFullSync + PerformFullSyncToFile.
│   ├── IncrementalSyncRunner.cs  PerformIncrementalSync + PerformIncrementalSyncToFile.
│   ├── CategoryHarvester.cs      GetAllCategories + WalkCategoryMap.
│   └── RevitSyncDialogCloser.cs  TryCloseRevitSyncDialog Win32 P/Invoke.
├── ui/                           Merged — all WinForms in one folder. forms/ deleted.
└── web/
    ├── ProjectPerseusWeb.cs      Moved from root.
    └── WebHelper.cs              Single class (de-duplicated from Utl.cs).

Util/JSON helpers from Utl.cs (JsonDump, PrettyWriteJson, SerializeToJson, IsValidUrl) move
to dedicated files under a util/ folder if their callers stay. WriteLog moves into
logging/Log.cs as the single logging entry point.

6.4 Refactor Priorities

P1  Split Plugin.cs → sync/ folder (highest dev-QoL win; touches the biggest file).
P2  Consolidate commands: rename commands/ → queue/, move IExternalCommands into commands/.
P3  Merge forms/ into ui/.
P4  Move root-level files into their existing namespace folders (auth/, config/, web/, revit/).
P5  De-duplicate WebHelper (single web/WebHelper.cs).
P6  Split AuthService.cs into auth/ sub-files.
P7  Unify logging: Log.cs becomes the front door; Utl.WriteLog disappears.

Each priority should be its own commit/build so any breakage is localised. Update §6.1 and
§6.3 in the same commit as the move.

6.5 Code-Organization Conventions

- Folder names are lowercase. Namespaces match folder paths exactly:
  `revit_plugin/src/ProjectPerseus/auth/X.cs` ⇒ `namespace ProjectPerseus.auth`.
- One public class per file; filename matches the class.
- IExternalCommand implementations belong in `commands/`, nowhere else.
- WinForms (and form designers) belong in `ui/`, nowhere else.
- Models/DTOs (no behaviour beyond serialisation) belong in `models/`.
- Revit API wrappers/adapters belong in `revit/` (Ardb* adapters in `revit/adapters/`).
- Sync orchestration logic (anything called from a Document* event) belongs in `sync/`.
- HTTP and JSON wire helpers belong in `web/`.
- Logging goes through `logging/Log.cs` (single front door). Do not add new file-output
  helpers elsewhere.
- Every new `.cs` file needs a `<Compile Include="..."/>` entry in ProjectPerseus.csproj
  (see feedback_csproj_include memory — the .csproj is old-style, no glob discovery).
- When you move or add a folder/file/namespace, update §6.1 and §6.3 in the SAME change.