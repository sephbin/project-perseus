using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ProjectPerseus.models;
using ProjectPerseus.sync;
using ProjectPerseus.ui;

namespace ProjectPerseus.queue
{
    public class BatchProcessor
    {
        private const int MaxAttempts = 5;
        private const int RetryWaitMs = 30_000;

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
                // Retry queue: failed models are re-enqueued at the end (up to MaxAttempts each).
                // If a full pass completes with no successes, sleep RetryWaitMs before continuing
                // so transient cloud issues have a chance to clear.
                var queue = new Queue<RetryEntry>(
                    _instruction.ModelsToProcess.Select(m => new RetryEntry { Model = m, Attempt = 1 }));
                int consecutiveFailures = 0;

                while (queue.Count > 0)
                {
                    if (progressForm.AbortRequested) break;

                    var entry = queue.Dequeue();
                    string label = entry.Attempt == 1
                        ? $"Opening Model: {entry.Model.DisplayName}..."
                        : $"Opening Model: {entry.Model.DisplayName} (attempt {entry.Attempt}/{MaxAttempts})...";
                    progressForm.UpdateStatus(label);

                    bool success = ProcessModel(entry.Model);
                    if (success)
                    {
                        consecutiveFailures = 0;
                        continue;
                    }

                    consecutiveFailures++;
                    if (entry.Attempt < MaxAttempts)
                    {
                        Utl.WriteLog($"Requeueing {entry.Model.DisplayName} for retry {entry.Attempt + 1}/{MaxAttempts}.", LogLevel.Warn);
                        queue.Enqueue(new RetryEntry { Model = entry.Model, Attempt = entry.Attempt + 1 });
                    }
                    else
                    {
                        Utl.WriteLog($"Giving up on {entry.Model.DisplayName} after {MaxAttempts} attempts.", LogLevel.Error);
                    }

                    // Full pass through the queue with zero successes -> back off before the next round.
                    if (queue.Count > 0 && consecutiveFailures >= queue.Count)
                    {
                        Utl.WriteLog($"No successes in the last pass; waiting 30s before retrying {queue.Count} model(s)...", LogLevel.Warn);
                        if (!SleepWithCountdown(RetryWaitMs, progressForm, $"Retrying {queue.Count} model(s) in")) break;
                        consecutiveFailures = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Critical Batch Error: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                Utl.WriteLog($"Stack: {ex.StackTrace}", LogLevel.Error);
            }
            finally
            {
                _uiApp.DialogBoxShowing -= SuppressDialogs;
                progressForm.Close();
                CleanupAndExit(true); // Kill Revit when done
            }
        }

        private bool ProcessModel(BatchModelInfo modelInfo)
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
                    Utl.WriteLog($"Building cloud path - Region: {regionCode}, Project: {projGuid}, Model: {modGuid}");
                    modelPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(regionCode, projGuid, modGuid);
                }

                OpenOptions openOptions = new OpenOptions();
                if (modelInfo.IsLocalFile)
                    openOptions.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;

                WorksetConfiguration worksetConfig = GetWorksetConfig(modelPath, modelInfo, out bool modelReachable);
                if (!modelReachable)
                {
                    // Revit already told us the model is missing/invalid via GetUserWorksetInfo.
                    // Calling OpenDocumentFile against it crashes Revit (no managed exception),
                    // so bail out and let the retry queue try again later.
                    Utl.WriteLog($"Skipping OpenDocumentFile for {modelInfo.DisplayName}: Revit reports model unreachable.", LogLevel.Warn);
                    return false;
                }

                openOptions.SetOpenWorksetsConfiguration(worksetConfig);

                Utl.WriteLog($"Calling OpenDocumentFile for {modelInfo.DisplayName}...");
                doc = _uiApp.Application.OpenDocumentFile(modelPath, openOptions);
                Utl.WriteLog($"OpenDocumentFile returned for {modelInfo.DisplayName}.");

                Utl.WriteLog($"Document opened: {doc.Title}. Running Perseus sync...");
                var revitFacade = new revit.RevitFacade(doc);

                if (_instruction.ExportMode == models.BatchExportMode.File
                    || _instruction.ExportMode == models.BatchExportMode.IncrementalFile)
                {
                    string outputDir = _instruction.OutputDirectory
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PerseusExports");

                    if (_instruction.ExportMode == models.BatchExportMode.IncrementalFile)
                        IncrementalSyncRunner.PerformIncrementalSyncToFile(revitFacade, outputDir, modelInfo.PerseusCategories, modelInfo.CollectConnectedElements);
                    else
                        FullSyncRunner.PerformFullSyncToFile(revitFacade, outputDir, modelInfo.PerseusCategories, modelInfo.CollectConnectedElements);
                }
                else
                {
                    IncrementalSyncRunner.PerformIncrementalSync(revitFacade);
                }

                Utl.WriteLog($"Successfully processed: {doc.Title}");
                return true;
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Failed to process model {modelInfo.DisplayName}: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                Utl.WriteLog($"Stack: {ex.StackTrace}", LogLevel.Error);
                return false;
            }
            finally
            {
                if (doc != null && doc.IsValidObject)
                {
                    try { doc.Close(false); }
                    catch (Exception ex) { Utl.WriteLog($"Error closing document: {ex.Message}", LogLevel.Warn); }
                }
            }
        }

        private WorksetConfiguration GetWorksetConfig(ModelPath modelPath, BatchModelInfo modelInfo, out bool modelReachable)
        {
            modelReachable = true;

            // Local files are opened detached — GetUserWorksetInfo can't pre-inspect them,
            // so fall back to opening all worksets rather than closing all.
            var fallback = modelInfo.IsLocalFile
                ? WorksetConfigurationOption.OpenAllWorksets
                : WorksetConfigurationOption.CloseAllWorksets;
            var config = new WorksetConfiguration(fallback);

            try
            {
                // Fetch workset data without opening the model
                IList<WorksetPreview> previews = WorksharingUtils.GetUserWorksetInfo(modelPath);
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
                string msg = ex.Message ?? string.Empty;
                bool revitFlaggedInvalid =
                    msg.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("not valid", StringComparison.OrdinalIgnoreCase) >= 0;

                Utl.WriteLog(
                    $"GetUserWorksetInfo failed for {modelInfo.DisplayName}: {ex.GetType().FullName}: {msg}",
                    LogLevel.Error);
                Utl.WriteLog($"Stack: {ex.StackTrace}", LogLevel.Error);

                if (revitFlaggedInvalid && !modelInfo.IsLocalFile)
                {
                    // Cloud model reported as missing/invalid — opening it crashes Revit.
                    // Flag unreachable so ProcessModel skips OpenDocumentFile and the retry queue can try again.
                    modelReachable = false;
                }
                else
                {
                    Utl.WriteLog($"Defaulting workset configuration to {fallback}.", LogLevel.Warn);
                }
            }

            return config;
        }

        private bool SleepWithCountdown(int totalMs, BatchProgressForm progressForm, string statusPrefix)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(totalMs);
            int lastSecShown = -1;

            while (true)
            {
                if (progressForm.AbortRequested) return false;

                var remaining = deadline - DateTime.UtcNow;
                if (remaining.TotalMilliseconds <= 0) return true;

                int sec = (int)Math.Ceiling(remaining.TotalSeconds);
                if (sec != lastSecShown)
                {
                    progressForm.UpdateStatus($"{statusPrefix} {sec}s...");
                    lastSecShown = sec;
                }

                Thread.Sleep(100);
                Application.DoEvents();
            }
        }

        private void SuppressDialogs(object sender, DialogBoxShowingEventArgs e)
        {
            // Automatically click "Cancel" or "Close" on all popups to prevent Revit freezing
            // This handles "Missing Links", "Missing Fonts", etc.
            e.OverrideResult((int)DialogResult.Cancel);
        }

        private class RetryEntry
        {
            public BatchModelInfo Model;
            public int Attempt;
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