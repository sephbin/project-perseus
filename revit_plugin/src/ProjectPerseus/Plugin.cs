using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using ProjectPerseus.sync;
using ProjectPerseus.ui;

using ProjectPerseus.logging;
namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class Plugin : IExternalApplication
    {
        private readonly SyncOrchestrator _orchestrator = new SyncOrchestrator();
        private Stopwatch _startupStopwatch;
        private string _batchFilePath;

        // Startup diagnostics: when Revit loaded us, how long each startup step took, when Revit first became idle.
        private static readonly Stopwatch SinceLoad = Stopwatch.StartNew();
        private static bool _readyLogged;
        private static volatile bool _ready;

        public Result OnStartup(UIControlledApplication application)
        {
            var clock = Stopwatch.StartNew();
            var steps = new System.Collections.Generic.List<string>();
            void Step(string name, Action a)
            {
                long t0 = clock.ElapsedMilliseconds;
                a();
                steps.Add($"{name} {clock.ElapsedMilliseconds - t0}");
            }
            string revitVersion = application.ControlledApplication.VersionNumber;
            string pluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            Log.InitSession(revitVersion, pluginVersion);
            Step("crash trace", CrashTrace.Install);
            Step("code pages", util.CodePages.Register);
            Step("sync", () => _orchestrator.Subscribe(application));
            Step("violation highlight", violations.ViolationHighlightController.Initialize);
            Step("change highlight", federator.ChangeHighlightController.Initialize);
            Step("ownership", () => ownership.OwnershipTracker.Subscribe(application));
            Step("markers", () => markers.MarkerEngine.Subscribe(application));
            Step("federator", () => federator.FederatorController.Subscribe(application));
            Step("ribbon", () => AddRibbonPanel(application));
            Step("icons", () => ThemeIconManager.Initialize(application));
            Log.Info($"[Startup] Perseus OnStartup {clock.ElapsedMilliseconds} ms (ms per step: {string.Join(", ", steps)})");
            application.Idling += OnFirstIdling;
            application.ControlledApplication.DocumentOpening += (s, e) => Log.Info($"[Startup] opening '{System.IO.Path.GetFileName(e.PathName)}' at +{SinceLoad.Elapsed.TotalSeconds:0} s");
            application.ControlledApplication.DocumentOpened += (s, e) => Log.Info($"[Startup] opened '{e.Document?.Title}' at +{SinceLoad.Elapsed.TotalSeconds:0} s");
            // Watchdog: until Revit is first idle, note every 20 s that it's still busy (shows how far startup got).
            var watchdog = new System.Threading.Thread(() =>
            {
                try
                {
                    for (int i = 1; i <= 30 && !_ready; i++)
                    {
                        System.Threading.Thread.Sleep(20000);
                        if (!_ready) Log.Info($"[Startup] Revit not idle yet: +{SinceLoad.Elapsed.TotalSeconds:0} s since Perseus loaded");
                    }
                }
                catch { /* diagnostics only */ }
            }) { IsBackground = true, Name = "Perseus startup watchdog" };
            watchdog.Start();

            // Batch trigger: if %AppData%\ProjectPerseus\batch_task.json exists, hand off to
            // BatchProcessor once Revit's UI has settled. The Idling event fires repeatedly,
            // so we use a Stopwatch to wait ~3s before unsubscribing and acting.
            string roamingFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _batchFilePath = Path.Combine(roamingFolder, "ProjectPerseus", "batch_task.json");

            if (File.Exists(_batchFilePath))
            {
                Log.Info("Batch task file detected. Hooking into Idling event with Stopwatch...");
                _startupStopwatch = Stopwatch.StartNew();
                application.Idling += OnRevitIdlingDelay;
            }

            // Always hook Idling for key schedule auto-import (onStart + onIdle triggers).
            application.Idling += OnKeyScheduleIdling;

            return Result.Succeeded;
        }

        private static void OnFirstIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_readyLogged) return;
            _readyLogged = true;
            _ready = true;
            Log.Info($"[Startup] Revit first idle (ready) at +{SinceLoad.Elapsed.TotalSeconds:0} s since Perseus loaded");
            try { ((UIApplication)sender).Idling -= OnFirstIdling; } catch { }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _orchestrator.Unsubscribe(application);
            ownership.OwnershipTracker.Unsubscribe(application);
            markers.MarkerEngine.Unsubscribe(application);
            federator.FederatorController.Unsubscribe(application);
            ThemeIconManager.Shutdown(application);
            return Result.Succeeded;
        }

        private static void AddRibbonPanel(UIControlledApplication application)
        {
            const string tabName = "Perseus";
            application.CreateRibbonTab(tabName);

            string asm = Assembly.GetExecutingAssembly().Location;
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

            // Panels are grouped by function: Sync / Element IDs / Key Schedules / Violations / Markers / Federator / Setup.
            // Icon base names map to resources/<name>.png (+ <name>_dark.png); see revit_plugin/tools/generate_icons.py.

            // ---- Sync: moving data between the model and the Perseus server
            RibbonPanel syncPanel = application.CreateRibbonPanel(tabName, "Sync");
            AddButton(syncPanel, asm, "Button_RunFullSync", "Upload", "PerformFullUploadCommand", "upload",
                "Upload all elements to external database");
            AddButton(syncPanel, asm, "Button_PullWebEdits", "Pull Web\nEdits", "PullWebEditsCommand", "pull_edits",
                "Fetch pending parameter edits made in the web editor and apply them to the current model. Skips elements checked out by other users.");
            AddButton(syncPanel, asm, "Button_Ownership", "Ownership", "ShowOwnershipCommand", "ownership",
                "See which elements and worksets each user owns in this model, and relinquish your own. The cyan number in the status bar is how many elements you own; click it to open this panel.");

            // ---- Element IDs
            RibbonPanel idsPanel = application.CreateRibbonPanel(tabName, "Element IDs");
            AddButton(idsPanel, asm, "Button_AuditIds", "Audit\nIDs", "AuditIdsCommand", "audit_ids",
                "Check every element against this project's ID rules and fix missing, duplicate or invalid IDs (e.g. after copying elements outside Perseus). Also fills Perseus_ID.");
            AddButton(idsPanel, asm, "Button_RenumberIds", "Renumber\nIDs", "RenumberIdsCommand", "renumber_ids",
                "Renumber every ID from 1 within each counter group (e.g. each level), in the numbering order set for the rule on the server, such as left to right then top to bottom. Replaces existing numbers; use at a deliberate milestone.");

            // ---- Key Schedules
            RibbonPanel schedulesPanel = application.CreateRibbonPanel(tabName, "Key Schedules");
            AddButton(schedulesPanel, asm, "Button_ManageSchedules", "Manage\nSchedules", "ManageSchedulesCommand", "schedule_manage",
                "Add, edit, or remove key schedule ↔ Excel mappings stored in this project file (active document only).");
            AddButton(schedulesPanel, asm, "Button_ExportSchedules", "Export\nSchedules", "ExportKeyScheduleCommand", "schedule_export",
                "Export key schedules to Excel for all open Revit models that have mappings configured.");
            AddButton(schedulesPanel, asm, "Button_ImportSchedules", "Import\nSchedules", "ImportKeyScheduleCommand", "schedule_import",
                "Import key schedule data from Excel into all open Revit models that have mappings configured.");

            // ---- Violations: highlight layer, list, and the emergency bypass
            RibbonPanel violationsPanel = application.CreateRibbonPanel(tabName, "Violations");
            AddButton(violationsPanel, asm, "Button_ToggleViolations", "Toggle\nViolations", "ToggleViolationHighlightCommand", "violations_toggle",
                "Show or hide the violation highlight layer (bounding boxes or rings) in the active view.");
            AddButton(violationsPanel, asm, "Button_SwitchMode", "Switch\nMode", "SwitchViolationModeCommand", "violations_mode",
                "Switch the violation highlight layer between bounding-box wireframes and rings around each element (hidden while you pan/zoom).");
            AddButton(violationsPanel, asm, "Button_ViolationList", "Violation\nList", "ShowViolationListCommand", "violations_list",
                "Open the violation list. Select an element in Revit to see its violations in the bottom panel.");
            AddButton(violationsPanel, asm, "Button_Freefall", "Freefall\nMode", "FreefallCommand", "freefall",
                "Enable Freefall mode to bypass violation blocks for one sync. Requires confirmation. Turns off automatically after sync.");

            // ---- Markers: rings/icons around elements by parameter value
            RibbonPanel markersPanel = application.CreateRibbonPanel(tabName, "Markers");
            AddButton(markersPanel, asm, "Button_Markers", "Parameter\nMarkers", "ShowMarkersCommand", "markers",
                "Ring elements in the view by a parameter value, with a colour and icon per value and a legend: e.g. doors with mag locks in magenta and drop bolts in cyan. Define what to look for (categories, filter, parameter) in the panel.");

            // ---- Federator: consultant link comparison + review
            RibbonPanel federatorPanel = application.CreateRibbonPanel(tabName, "Federator");
            AddButton(federatorPanel, asm, "Button_Federator", "Link\nChanges", "ShowFederatorCommand", "federator",
                "Compare a consultant's linked model with an earlier version of it and review what changed: filter the changes, see how each one changed (and what probably replaced deleted elements), set a status, assign actions and comment. Reviews are saved in this model.");

            // Family mapping: our families at the consultants' locations
            AddButton(federatorPanel, asm, "Button_FamilyMapping", "Family\nMapping", "ShowFamilyMappingCommand", "family_mapping",
                "Place our families where a consultant's are (or link ones already in the model): map each consultant family type to one of ours with an offset, rotation and workset, test it on one element, then place or link them all. Each of ours remembers the consultant element it stands for. Types reviewed in Link Changes triage are suggested.");

            // ---- Setup: configuration up front, rarely used tools stacked small, About standalone
            RibbonPanel setupPanel = application.CreateRibbonPanel(tabName, "Setup");
            AddButton(setupPanel, asm, "Button_ProjectRules", "Project\nRules", "ProjectRulesCommand", "project_rules",
                "View and edit this model's violation enforcement rules and source settings. Refresh to pull the latest configuration from the server.");
            AddButton(setupPanel, asm, "Button_Settings", "Settings", "OpenSettingsCommand", "settings",
                "Change Perseus Settings like: API Token and Upload URL");

            var stacked = setupPanel.AddStackedItems(
                MakeData(asm, "Button_ResetGuid", "Reset Identity", "ResetModelGuidCommand"),
                MakeData(asm, "Button_DiagnoseElement", "Diagnose Elements", "DiagnoseElementCommand"));
            StyleButton(stacked[0] as PushButton, "reset_identity",
                "Generates a new Database GUID for this model. Use ONLY if this file was copied from an older project.");
            StyleButton(stacked[1] as PushButton, "diagnose",
                "Enter element IDs to run a full diagnostic from the plugin's perspective and write results to the log file.");

            AddButton(setupPanel, asm, "Button_About", "About\nPerseus", "AboutCommand", "about",
                $"Project Perseus v{version} — click to view version info");
        }

        private static PushButtonData MakeData(string asm, string name, string text, string command)
        {
            return new PushButtonData(name, text, asm, "ProjectPerseus.commands." + command);
        }

        private static void StyleButton(PushButton button, string icon, string tooltip)
        {
            if (button == null) return;
            button.ToolTip = tooltip;
            ThemeIconManager.Register(button, icon);
        }

        private static void AddButton(RibbonPanel panel, string asm, string name, string text, string command, string icon, string tooltip)
        {
            StyleButton(panel.AddItem(MakeData(asm, name, text, command)) as PushButton, icon, tooltip);
        }

        private void OnKeyScheduleIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (sender is UIApplication uiApp)
                sync.KeyScheduleAutoImporter.HandleIdling(uiApp);
        }

        private void OnRevitIdlingDelay(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_startupStopwatch.ElapsedMilliseconds < 3000) return;

            // Time's up. Unsubscribe immediately so this never runs again.
            var uiApp = sender as UIApplication;
            if (uiApp != null)
            {
                uiApp.Idling -= OnRevitIdlingDelay;
            }
            _startupStopwatch.Stop();

            Log.Info($"[Plugin] Idling delay finished. Actual elapsed: {_startupStopwatch.ElapsedMilliseconds}ms. Evaluating Batch Task...");

            try
            {
                string json = File.ReadAllText(_batchFilePath);
                var instruction = JsonConvert.DeserializeObject<models.BatchInstruction>(json);

                if (instruction != null && instruction.IsValid())
                {
                    Log.Info("[Plugin] Batch task is valid. Launching Batch Processor...");
                    var processor = new queue.BatchProcessor(uiApp, instruction, _batchFilePath);
                    processor.Start();
                }
                else
                {
                    Log.Info("[Plugin] Batch task is expired or invalid. Deleting file and ignoring.");
                    File.Delete(_batchFilePath);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"[Plugin] Critical failure in boot trigger: {ex.Message}");
                if (File.Exists(_batchFilePath)) File.Delete(_batchFilePath);
            }
        }
    }
}
