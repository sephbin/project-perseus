using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ProjectPerseus.models;
using ProjectPerseus.ui;

namespace ProjectPerseus.queue
{
    public class BatchProcessor
    {
        private readonly UIApplication _uiApp;
        private readonly BatchInstruction _instruction;
        private readonly string _taskFilePath;

        public BatchProcessor(UIApplication uiApp, BatchInstruction instruction, string taskFilePath)
        {
            _uiApp = uiApp;
            _instruction = instruction;
            _taskFilePath = taskFilePath;
        }

        public void Start()
        {
            // 1. Run the 60-second countdown
            using (var countdownForm = new BatchProgressForm(true))
            {
                var result = countdownForm.ShowDialog();
                if (result == DialogResult.Cancel)
                {
                    Utl.WriteLog("User aborted batch process during countdown.");
                    CleanupAndExit(false);
                    return;
                }
            }

            // 2. Start the floating progress tracker
            var progressForm = new BatchProgressForm(false);
            progressForm.Show();

            // 3. Suppress UI Dialogs
            _uiApp.DialogBoxShowing += SuppressDialogs;

            try
            {
                foreach (var modelInfo in _instruction.ModelsToProcess)
                {
                    if (progressForm.AbortRequested) break;

                    progressForm.UpdateStatus($"Opening Model: {modelInfo.DisplayName}...");
                    ProcessModel(modelInfo);
                }
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Critical Batch Error: {ex.Message}");
            }
            finally
            {
                _uiApp.DialogBoxShowing -= SuppressDialogs;
                progressForm.Close();
                CleanupAndExit(true); // Kill Revit when done
            }
        }

        private void ProcessModel(BatchModelInfo modelInfo)
        {
            Document doc = null;
            try
            {
                ModelPath modelPath;

                if (modelInfo.IsLocalFile)
                {
                    Utl.WriteLog($"Opening local file: {modelInfo.LocalPath}");
                    modelPath = new FilePath(modelInfo.LocalPath);
                }
                else
                {
                    Guid projGuid = Guid.Parse(modelInfo.ProjectGuid);
                    Guid modGuid = Guid.Parse(modelInfo.ModelGuid);
                    string regionCode = string.IsNullOrEmpty(modelInfo.Region) ? "US" : modelInfo.Region;
                    Utl.WriteLog($"Building cloud path for Region: {regionCode}...");
                    modelPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(regionCode, projGuid, modGuid);
                }

                OpenOptions openOptions = new OpenOptions();
                WorksetConfiguration worksetConfig = GetWorksetConfig(modelPath, modelInfo);
                openOptions.SetOpenWorksetsConfiguration(worksetConfig);

                doc = _uiApp.Application.OpenDocumentFile(modelPath, openOptions);

                Utl.WriteLog($"Document opened: {doc.Title}. Running Perseus sync...");
                var revitFacade = new revit.RevitFacade(doc);

                if (_instruction.ExportMode == models.BatchExportMode.File)
                {
                    string outputDir = _instruction.OutputDirectory
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PerseusExports");
                    Plugin.PerformFullSyncToFile(revitFacade, outputDir, modelInfo.PerseusCategories, modelInfo.CollectConnectedElements);
                }
                else
                {
                    Plugin.PerformIncrementalSync(revitFacade);
                }

                Utl.WriteLog($"Successfully processed: {doc.Title}");
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Failed to process model {modelInfo.DisplayName}: {ex.Message}");
            }
            finally
            {
                if (doc != null && doc.IsValidObject)
                    doc.Close(false);
            }
        }

        private WorksetConfiguration GetWorksetConfig(ModelPath cloudPath, BatchModelInfo modelInfo)
        {
            var config = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);

            try
            {
                // Fetch workset data without opening the model
                IList<WorksetPreview> previews = WorksharingUtils.GetUserWorksetInfo(cloudPath);
                List<WorksetId> worksetsToOpen = new List<WorksetId>();

                Regex whitelist = string.IsNullOrEmpty(modelInfo.WorksetWhitelistRegex) ? null : new Regex(modelInfo.WorksetWhitelistRegex, RegexOptions.IgnoreCase);
                Regex blacklist = string.IsNullOrEmpty(modelInfo.WorksetBlacklistRegex) ? null : new Regex(modelInfo.WorksetBlacklistRegex, RegexOptions.IgnoreCase);

                foreach (var ws in previews)
                {
                    bool openThis = false;

                    // If there is a whitelist, check it. If no whitelist, assume we want to open it (unless blacklisted).
                    if (whitelist != null)
                    {
                        if (whitelist.IsMatch(ws.Name)) openThis = true;
                    }
                    else
                    {
                        openThis = true;
                    }

                    // Blacklist overrides whitelist
                    if (blacklist != null && blacklist.IsMatch(ws.Name))
                    {
                        openThis = false;
                    }

                    if (openThis) worksetsToOpen.Add(ws.Id);
                }

                if (worksetsToOpen.Count > 0)
                {
                    config.Open(worksetsToOpen);
                }
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Failed to configure worksets: {ex.Message}. Defaulting to close all.");
            }

            return config;
        }

        private void SuppressDialogs(object sender, DialogBoxShowingEventArgs e)
        {
            // Automatically click "Cancel" or "Close" on all popups to prevent Revit freezing
            // This handles "Missing Links", "Missing Fonts", etc.
            e.OverrideResult((int)DialogResult.Cancel);
        }

        // Terminates the current process with a specific exit code, bypassing CLR shutdown
        // hooks entirely. This avoids plugin unload dialogs (e.g. Rhino.Inside) that would
        // block Environment.Exit() or a normal Revit close.
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

        private void CleanupAndExit(bool killRevit)
        {
            try
            {
                if (File.Exists(_taskFilePath)) File.Delete(_taskFilePath);
            }
            catch { }

            if (killRevit)
            {
                Utl.WriteLog("Batch complete. Terminating Revit process.");

                // TerminateProcess with exit code 0: identical to Process.Kill() but the
                // process exits with code 0, so Task Scheduler marks the run as succeeded
                // rather than crashed. No CLR shutdown or plugin unload hooks fire.
                TerminateProcess(GetCurrentProcess(), 0u);
            }
        }
    }
}