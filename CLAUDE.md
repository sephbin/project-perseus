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

6.1 Current Layout (snapshot 2026-05-30, after P1 + P2 + P3 + P4)

ProjectPerseus/
├── Plugin.cs                     ~130 lines — IExternalApplication lifecycle, AddRibbonPanel,
│                                 batch trigger (Idling event → BatchProcessor handoff). Owns
│                                 a SyncOrchestrator and forwards Subscribe/Unsubscribe.
│                                 Ribbon strings reference ProjectPerseus.commands.*.
├── Log.cs                        Static logger → Sentry + console. Stays at root until P7
│                                 logging unification (moves to logging/ then).
├── Utl.cs                        Kitchen sink: WriteLog (file), JsonDump, SerializeToJson,
│                                 IsValidUrl, nested SentryContext. P7 will fold into logging/.
├── auth/
│   └── AuthService.cs            434 lines — MSAL + JWT + PAT + token cache + HTTP config.
│                                 To be split in P6.
├── batch/
│   └── BatchProcessor.cs         In ProjectPerseus.queue namespace (folder/ns mismatch).
├── config/                       NEW (P4).
│   └── Config.cs                 Namespace ProjectPerseus → ProjectPerseus.config.
├── commands/                     ONLY IExternalCommand classes now (P2, 2026-05-30).
│   ├── EditSettingsCommand.cs    DUPLICATE of OpenSettingsCommand — unwired, candidate for deletion.
│   ├── InitialiseProjectCommand.cs  Body is `// todo` — unwired, candidate for deletion.
│   ├── OpenSettingsCommand.cs    Ribbon: Button_Settings.
│   ├── PerformFullUploadCommand.cs  Ribbon: Button_RunFullSync.
│   └── ResetModelGuidCommand.cs  Ribbon: Button_ResetGuid.
├── queue/                        Renamed from commands/ in P2 (folder now matches namespace).
│   ├── AutoSyncEvent.cs          IExternalEventHandler; flips SyncOrchestrator.IsAutoSyncing.
│   └── QueuePoller.cs            Background task watching /syncboat/api/v2/source/<guid>/queue/.
├── ui/                           ALL WinForms (P3, 2026-05-30). forms/ folder deleted.
│   ├── AutoSyncCountdownForm.cs  Moved from forms/ (namespace was already ProjectPerseus.ui).
│   ├── BatchProgressForm.cs
│   ├── JwtLoginForm.cs           Moved from forms/, namespace ProjectPerseus.forms → .ui.
│   ├── QueueWebForm.cs           Moved from forms/, namespace ProjectPerseus.forms → .ui.
│   ├── SettingsForm.cs           Moved from forms/, namespace ProjectPerseus → .ui.
│   ├── SettingsForm.Designer.cs  Partial; namespace matches SettingsForm.cs.
│   ├── SettingsForm.resx         Embedded resource; DependentUpon resolves manifest name via
│                                 typeof(SettingsForm).FullName, so path change is safe.
│   ├── SyncWarningForm.cs        Moved from forms/. Was at global namespace; now ProjectPerseus.ui.
│   └── ThemeIconManager.cs
├── models/                       DTOs + geometry extractor.
│   └── geometry/                 (see feedback_csproj_include.md for ARDB alias rule)
├── revit/                        Revit API adapter layer (RevitFacade, extractors).
│   ├── adapters/                 Ardb* wrappers around Autodesk.Revit.DB types.
│   ├── interfaces/               IArdb* interfaces.
│   ├── BatchFailureHandler.cs    12 lines — belongs with batch/ (will move in a later pass).
│   └── ModelGuidStorage.cs       Moved from root in P4 (namespace already matched).
├── sync/                         All sync orchestration + runners (P1, 2026-05-30).
│   ├── SyncOrchestrator.cs       Instance class owning sync state + DocumentSynchronizing/zed
│                                 handlers + ProgressChanged. Statics: IsAutoSyncing,
│                                 AutoSyncExternalEvent.
│   ├── FullSyncRunner.cs         PerformFullSync (Django) + PerformFullSyncToFile (JSON file).
│   ├── IncrementalSyncRunner.cs  PerformIncrementalSync + PerformIncrementalSyncToFile +
│                                 private ReadFirstSourceState helper.
│   ├── CategoryHarvester.cs      GetAllCategories + WalkCategoryMap (3-pass dedup).
│   ├── StateSubmitter.cs         Thin wrappers over ProjectPerseusWeb (moves to web/ in P5).
│   └── RevitSyncDialogCloser.cs  Win32 P/Invoke: TryClose() finds "Sync With Central" dialogs
│                                 by title and PostMessage(WM_CLOSE).
├── web/
│   ├── ProjectPerseusWeb.cs      HTTP client for Django: SubmitElementDeltas (HttpClient +
│                                 AuthService scheme) and SubmitElementState (legacy Token via
│                                 shared WebHelper with scheme="Token").
│   └── WebHelper.cs              NEW (P5). Single static WebHelper. Optional scheme param
│                                 (defaults "Bearer") so legacy "Token" call still works.
├── Properties/                   AssemblyInfo + designer files (do not touch).
└── resources/                    Icons (.png that are actually ICO containers — §4 rule).

6.2 Known Organization Smells (ranked by impact)

S1  [DONE — P1, 2026-05-30] Plugin.cs split into sync/ folder. Down from 1139 → ~130 lines.
S2  [DONE — P2, 2026-05-30] commands/ holds only IExternalCommand classes; queue infra
    moved to queue/. Note: EditSettingsCommand + InitialiseProjectCommand are unwired
    and candidates for deletion in a follow-up cleanup.
S3  [DONE — P3, 2026-05-30] All WinForms live in ui/; forms/ deleted. SyncWarningForm
    received a proper namespace (was at global). SettingsForm + Designer moved out of root.
S4  Two parallel logging systems: Utl.WriteLog (file) vs Log (Sentry). Pick one front door.
S5  Utl.cs is a kitchen sink (logging, JSON, URL, SentryContext). WebHelper duplication
    [DONE — P5, 2026-05-30]: single web/WebHelper.cs; nested copies deleted; ProjectPerseusWeb's
    legacy "Token" call now passes the scheme explicitly. Remainder folds into P7.
S6  AuthService.cs (434 lines) bundles MSAL, JWT, PAT, refresh-token persistence, token cache.
S7  Namespace ↔ folder mismatches: batch/ → ProjectPerseus.queue, commands/ → .queue,
    ModelGuidStorage.cs (root) → .revit, AuthService.cs (root) → .auth.
S8  Namespace casing inconsistency: ProjectPerseus.Commands (Pascal) vs everything else
    (lowercase). Lowercase is the de-facto norm — align Commands to commands.
S9  [DONE — P4, 2026-05-30] Root-level orphans (AuthService, Config, ModelGuidStorage,
    ProjectPerseusWeb) moved into their namespace folders. Log + Utl remain at root
    pending P7 logging unification.

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
├── sync/                         [DONE — P1] See §6.1 for the as-built layout.
│                                 Decision: consolidated PreSyncQueue + PostSyncDispatcher into
│                                 a single SyncOrchestrator because they share too much state
│                                 (_isSyncing, _queueReleasedEarly, _currentSyncDoc) for a
│                                 three-way split to be cleaner than a one-class orchestrator.
├── ui/                           Merged — all WinForms in one folder. forms/ deleted.
└── web/
    ├── ProjectPerseusWeb.cs      Moved from root.
    └── WebHelper.cs              Single class (de-duplicated from Utl.cs).

Util/JSON helpers from Utl.cs (JsonDump, PrettyWriteJson, SerializeToJson, IsValidUrl) move
to dedicated files under a util/ folder if their callers stay. WriteLog moves into
logging/Log.cs as the single logging entry point.

6.4 Refactor Priorities

P1  [DONE 2026-05-30] Split Plugin.cs → sync/ folder.
P2  [DONE 2026-05-30] Consolidated commands: commands/ holds only IExternalCommands;
    queue/ holds AutoSyncEvent + QueuePoller. revit/plugin/ folder removed.
P3  [DONE 2026-05-30] Merged forms/ into ui/; all WinForms now in ProjectPerseus.ui namespace.
P4  [DONE 2026-05-30] Root-level files moved into folders. AuthService→auth/,
    ModelGuidStorage→revit/ (no namespace change). Config→config/, ProjectPerseusWeb→web/
    (with namespace fixes ProjectPerseus → ProjectPerseus.config/web).
P5  [DONE 2026-05-30] Single web/WebHelper.cs. Nested copies in Utl.cs and ProjectPerseusWeb.cs
    deleted. Optional `scheme` param (default "Bearer") preserves ProjectPerseusWeb's legacy
    "Token" call. Fixed a latent bug in the old ProjectPerseusWeb copy: it wrote a body even
    on GET and always set the Authorization header (sending "Token " on empty tokens). The
    unified implementation skips both when inputs are empty.
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