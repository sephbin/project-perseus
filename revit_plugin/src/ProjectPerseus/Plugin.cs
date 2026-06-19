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

        public Result OnStartup(UIControlledApplication application)
        {
            _orchestrator.Subscribe(application);
            AddRibbonPanel(application);
            ThemeIconManager.Initialize(application);

            string revitVersion = application.ControlledApplication.VersionNumber;
            string pluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            Log.InitSession(revitVersion, pluginVersion);

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

        public Result OnShutdown(UIControlledApplication application)
        {
            _orchestrator.Unsubscribe(application);
            ThemeIconManager.Shutdown(application);
            return Result.Succeeded;
        }

        private static void AddRibbonPanel(UIControlledApplication application)
        {
            const string tabName = "Perseus";
            application.CreateRibbonTab(tabName);

            RibbonPanel ribbonPanel = application.CreateRibbonPanel(tabName, "Tools");

            string thisAssemblyPath = Assembly.GetExecutingAssembly().Location;

            PushButtonData b1Data = new PushButtonData(
                "Button_RunFullSync",
                "Upload",
                thisAssemblyPath,
                "ProjectPerseus.commands.PerformFullUploadCommand");

            PushButton pb1 = ribbonPanel.AddItem(b1Data) as PushButton;
            pb1.ToolTip = "Upload all elements to external database";
            ThemeIconManager.Register(pb1, "perseus");

            PushButtonData settingsBtnData = new PushButtonData(
                "Button_Settings",
                "Settings",
                thisAssemblyPath,
                "ProjectPerseus.commands.OpenSettingsCommand");

            PushButton settingsBtn = ribbonPanel.AddItem(settingsBtnData) as PushButton;
            settingsBtn.ToolTip = "Change Perseus Settings like: API Token and Upload URL";
            ThemeIconManager.Register(settingsBtn, "settings");

            PushButtonData resetBtnData = new PushButtonData(
                "Button_ResetGuid",
                "Reset Identity",
                thisAssemblyPath,
                "ProjectPerseus.commands.ResetModelGuidCommand");

            PushButton resetBtn = ribbonPanel.AddItem(resetBtnData) as PushButton;
            resetBtn.ToolTip = "Generates a new Database GUID for this model. Use ONLY if this file was copied from an older project.";
            ThemeIconManager.Register(resetBtn, "reset");

            PushButtonData diagBtnData = new PushButtonData(
                "Button_DiagnoseElement",
                "Diagnose\nElements",
                thisAssemblyPath,
                "ProjectPerseus.commands.DiagnoseElementCommand");

            PushButton diagBtn = ribbonPanel.AddItem(diagBtnData) as PushButton;
            diagBtn.ToolTip = "Enter element IDs to run a full diagnostic from the plugin's perspective and write results to the log file.";

            // Key Schedules panel — separate compartment on the same tab
            RibbonPanel schedulesPanel = application.CreateRibbonPanel(tabName, "Key Schedules");

            PushButtonData manageSchedulesBtnData = new PushButtonData(
                "Button_ManageSchedules",
                "Manage\nSchedules",
                thisAssemblyPath,
                "ProjectPerseus.commands.ManageSchedulesCommand");

            PushButton manageSchedulesBtn = schedulesPanel.AddItem(manageSchedulesBtnData) as PushButton;
            manageSchedulesBtn.ToolTip = "Add, edit, or remove key schedule ↔ Excel mappings stored in this project file (active document only).";

            PushButtonData exportSchedulesBtnData = new PushButtonData(
                "Button_ExportSchedules",
                "Export\nSchedules",
                thisAssemblyPath,
                "ProjectPerseus.commands.ExportKeyScheduleCommand");

            PushButton exportSchedulesBtn = schedulesPanel.AddItem(exportSchedulesBtnData) as PushButton;
            exportSchedulesBtn.ToolTip = "Export key schedules to Excel for all open Revit models that have mappings configured.";

            PushButtonData importSchedulesBtnData = new PushButtonData(
                "Button_ImportSchedules",
                "Import\nSchedules",
                thisAssemblyPath,
                "ProjectPerseus.commands.ImportKeyScheduleCommand");

            PushButton importSchedulesBtn = schedulesPanel.AddItem(importSchedulesBtnData) as PushButton;
            importSchedulesBtn.ToolTip = "Import key schedule data from Excel into all open Revit models that have mappings configured.";
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
