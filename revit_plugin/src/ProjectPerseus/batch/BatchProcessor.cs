using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ProjectPerseus.models;
using ProjectPerseus.sync;
using ProjectPerseus.ui;

using Newtonsoft.Json;

using ProjectPerseus.logging;
namespace ProjectPerseus.queue
{
    public class BatchProcessor
    {
        private const int MaxAttempts = 3;        // Total attempts per model across all Revit sessions before giving up.
        private const int RecoverDelayMs = 5_000; // Pause between successful model transitions (defensive padding).
        private const int SessionIterationCap = 50; // Sanity cap to avoid infinite loops within one Revit session.

        private enum ProcessResult
        {
            Success,           // Sync ran cleanly.
            RetryableFailure,  // Generic failure - safe to keep going in the same Revit session.
            PoisonedSession,   // CentralModelException on a cloud model - Revit has a time-bomb queued; must exit before it fires.
        }

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
                    Log.Info("User aborted batch process during countdown.");
                    CleanupAndExit(killRevit: false, exitCode: 0);
                    return;
                }
            }

            // 2. Start the floating progress tracker
            var progressForm = new BatchProgressForm(false);
            progressForm.Show();

            // 3. Suppress UI Dialogs
            _uiApp.DialogBoxShowing += SuppressDialogs;

            int exitCode = 0;

            try
            {
                // No in-memory queue any more — batch_task.json is the source of truth and
                // is mutated as work progresses. On poison we persist + TerminateProcess(1)
                // so Task Scheduler relaunches Revit; on success we strip the model from the
                // file and continue. SessionIterationCap guards against an unexpected loop.
                int iter = 0;
                while (_instruction.ModelsToProcess.Count > 0 && iter < SessionIterationCap)
                {
                    iter++;
                    if (progressForm.AbortRequested) { Log.Info("[Loop] Aborted by progress form."); break; }

                    var modelInfo = _instruction.ModelsToProcess[0];
                    Log.Info($"[Loop] Iter {iter}: processing {modelInfo.DisplayName} (prior attempts: {modelInfo.Attempts}); {_instruction.ModelsToProcess.Count} model(s) remaining in batch.");
                    progressForm.UpdateStatus($"Opening Model: {modelInfo.DisplayName}...");

                    var result = ProcessModel(modelInfo);

                    if (result == ProcessResult.Success)
                    {
                        Log.Info($"[Loop] {modelInfo.DisplayName} OK. Removing from batch_task.json.");
                        _instruction.ModelsToProcess.RemoveAt(0);
                        SavePersistentState();

                        if (_instruction.ModelsToProcess.Count > 0 && !RecoverBetweenModels(progressForm)) break;
                        continue;
                    }

                    if (result == ProcessResult.PoisonedSession)
                    {
                        // GetUserWorksetInfo on a bad cloud model has queued an async file-open
                        // on a Revit worker thread that will crash Revit with an unhandled
                        // native exception within ~5s. We MUST exit before that fires, so we
                        // skip the normal recovery sleep entirely.
                        modelInfo.Attempts++;
                        if (modelInfo.Attempts >= MaxAttempts)
                        {
                            Log.Error($"[Loop] Giving up on {modelInfo.DisplayName} after {modelInfo.Attempts} poison failures. Removing from batch.");
                            _instruction.ModelsToProcess.RemoveAt(0);
                        }
                        else
                        {
                            Log.Warn($"[Loop] {modelInfo.DisplayName} poisoned Revit (attempt {modelInfo.Attempts}/{MaxAttempts}). Moving to end of queue; exiting for Task Scheduler relaunch.");
                            _instruction.ModelsToProcess.RemoveAt(0);
                            _instruction.ModelsToProcess.Add(modelInfo);
                        }
                        SavePersistentState();

                        // Exit code 1 when more work remains so the scheduler restarts us;
                        // 0 if we just removed the last model (clean batch end).
                        exitCode = _instruction.ModelsToProcess.Count > 0 ? 1 : 0;
                        Log.Warn($"[Loop] Terminating Revit now (exit {exitCode}) to skip GetUserWorksetInfo time-bomb.");
                        return; // finally block handles CleanupAndExit
                    }

                    // RetryableFailure — non-poison, safe to keep this Revit session alive.
                    modelInfo.Attempts++;
                    if (modelInfo.Attempts >= MaxAttempts)
                    {
                        Log.Error($"[Loop] Giving up on {modelInfo.DisplayName} after {modelInfo.Attempts} attempts. Removing from batch.");
                        _instruction.ModelsToProcess.RemoveAt(0);
                    }
                    else
                    {
                        Log.Warn($"[Loop] {modelInfo.DisplayName} failed (attempt {modelInfo.Attempts}/{MaxAttempts}). Moving to end of queue; staying in same Revit session.");
                        _instruction.ModelsToProcess.RemoveAt(0);
                        _instruction.ModelsToProcess.Add(modelInfo);
                    }
                    SavePersistentState();

                    if (_instruction.ModelsToProcess.Count > 0 && !RecoverBetweenModels(progressForm)) break;
                }

                if (iter >= SessionIterationCap)
                {
                    Log.Warn($"[Loop] Hit session iteration cap ({SessionIterationCap}). Exiting non-zero so scheduler relaunches.");
                    exitCode = 1;
                }
                else if (_instruction.ModelsToProcess.Count == 0)
                {
                    Log.Info("[Loop] Batch complete — no models remaining.");
                    exitCode = 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Critical Batch Error: {ex.GetType().Name}: {ex.Message}");
                Log.Error($"Stack: {ex.StackTrace}");
                // Unknown error — keep the file so the scheduler can retry.
                exitCode = 1;
            }
            finally
            {
                _uiApp.DialogBoxShowing -= SuppressDialogs;
                progressForm.Close();
                CleanupAndExit(killRevit: true, exitCode: exitCode);
            }
        }

        private void SavePersistentState()
        {
            try
            {
                _instruction.Timestamp = DateTime.Now;
                string json = JsonConvert.SerializeObject(_instruction, Formatting.Indented);
                // Write to a temp file then move atomically so a TerminateProcess from the
                // time-bomb mid-write can never leave a half-written batch_task.json behind.
                string tmp = _taskFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_taskFilePath))
                    File.Replace(tmp, _taskFilePath, null);
                else
                    File.Move(tmp, _taskFilePath);
                Log.Info($"[State] Persisted batch_task.json — {_instruction.ModelsToProcess.Count} model(s) remaining.");
            }
            catch (Exception ex)
            {
                Log.Error($"[State] Failed to persist batch_task.json: {ex.Message}");
            }
        }

        private ProcessResult ProcessModel(BatchModelInfo modelInfo)
        {
            Log.Info($"[ProcessModel] Entered for {modelInfo.DisplayName} (IsLocalFile={modelInfo.IsLocalFile}).");
            Document doc = null;
            try
            {
                ModelPath modelPath;

                if (modelInfo.IsLocalFile)
                {
                    Log.Info($"Opening local file: {modelInfo.LocalPath}");
                    modelPath = new FilePath(modelInfo.LocalPath);
                }
                else
                {
                    Guid projGuid = Guid.Parse(modelInfo.ProjectGuid);
                    Guid modGuid = Guid.Parse(modelInfo.ModelGuid);
                    string regionCode = string.IsNullOrEmpty(modelInfo.Region) ? "US" : modelInfo.Region;
                    Log.Info($"Building cloud path - Region: {regionCode}, Project: {projGuid}, Model: {modGuid}");
                    modelPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(regionCode, projGuid, modGuid);
                }

                // Read file headers before opening so we know what we're dealing with.
                // IsWorkshared=False on a file that we try to detach can cause DocWarnDialog + cancel.
                BasicFileInfo fileInfo = null;
                if (modelInfo.IsLocalFile)
                {
                    try
                    {
                        fileInfo = BasicFileInfo.Extract(modelInfo.LocalPath);
                        Log.Info($"[FileInfo] IsWorkshared={fileInfo.IsWorkshared}, Format='{fileInfo.Format}', SavedBy='{fileInfo.Username}'");
                    }
                    catch (Exception fiEx)
                    {
                        Log.Warn($"[FileInfo] BasicFileInfo.Extract failed: {fiEx.Message}");
                    }
                }

                OpenOptions openOptions = new OpenOptions();
                if (modelInfo.IsLocalFile)
                {
                    bool workshared = fileInfo?.IsWorkshared ?? true;
                    if (workshared)
                    {
                        // Attempt 0: preserve worksets (preferred — keeps workset structure intact).
                        // Attempt 1+: discard worksets — skips ownership resolution, which can fail on
                        // files with stale/unresolvable workset records and surface as DocWarnDialog + cancel.
                        var detachMode = modelInfo.Attempts == 0
                            ? DetachFromCentralOption.DetachAndPreserveWorksets
                            : DetachFromCentralOption.DetachAndDiscardWorksets;
                        openOptions.DetachFromCentralOption = detachMode;
                        Log.Info($"[ProcessModel] DetachMode={detachMode} (prior attempts: {modelInfo.Attempts})");
                    }
                    else
                    {
                        Log.Info("[FileInfo] File is not workshared — DetachFromCentralOption skipped.");
                    }
                }

                WorksetConfiguration worksetConfig = GetWorksetConfig(modelPath, modelInfo, out bool modelReachable);
                if (!modelReachable)
                {
                    // Cloud GetUserWorksetInfo just threw CentralModelException. Revit has
                    // already queued an async file-open on a worker thread that will crash
                    // the process with an unhandled native exception within ~5s. Caller
                    // must persist state and TerminateProcess before that fires.
                    Log.Warn($"Skipping OpenDocumentFile for {modelInfo.DisplayName}: Revit reports model unreachable. Session is now POISONED.");
                    return ProcessResult.PoisonedSession;
                }

                openOptions.SetOpenWorksetsConfiguration(worksetConfig);

                Log.Info($"Calling OpenDocumentFile for {modelInfo.DisplayName}...");
                doc = _uiApp.Application.OpenDocumentFile(modelPath, openOptions);
                Log.Info($"OpenDocumentFile returned for {modelInfo.DisplayName}.");

                Log.Info($"Document opened: {doc.Title}. Running Perseus sync...");
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

                Log.Info($"Successfully processed: {doc.Title}");
                return ProcessResult.Success;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to process model {modelInfo.DisplayName}: {ex.GetType().Name}: {ex.Message}");
                Log.Error($"Stack: {ex.StackTrace}");
                return ProcessResult.RetryableFailure;
            }
            finally
            {
                if (doc != null && doc.IsValidObject)
                {
                    try { doc.Close(false); }
                    catch (Exception ex) { Log.Warn($"Error closing document: {ex.Message}"); }
                }
            }
        }

        private WorksetConfiguration GetWorksetConfig(ModelPath modelPath, BatchModelInfo modelInfo, out bool modelReachable)
        {
            modelReachable = true;

            // Local files are opened detached — GetUserWorksetInfo can't pre-inspect them
            // (cloud-origin local copies cause Revit to hang waiting for cloud auth).
            // Just open all worksets and skip the whitelist/blacklist pass for local files.
            var fallback = modelInfo.IsLocalFile
                ? WorksetConfigurationOption.OpenAllWorksets
                : WorksetConfigurationOption.CloseAllWorksets;
            var config = new WorksetConfiguration(fallback);

            if (modelInfo.IsLocalFile)
            {
                Log.Info($"[GetWorksetConfig] Local file — skipping GetUserWorksetInfo, opening all worksets.");
                return config;
            }

            try
            {
                // Diagnostic: bracket the GetUserWorksetInfo call so a Revit crash shows
                // whether it happened inside the call (no "returned N worksets" line follows)
                // vs in our own code afterwards.
                Log.Info($"About to call GetUserWorksetInfo for {modelInfo.DisplayName}...");
                IList<WorksetPreview> previews = WorksharingUtils.GetUserWorksetInfo(modelPath);
                Log.Info($"GetUserWorksetInfo returned {previews.Count} workset(s) for {modelInfo.DisplayName}.");
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

                Log.Error($"GetUserWorksetInfo failed for {modelInfo.DisplayName}: {ex.GetType().FullName}: {msg}");
                Log.Error($"Stack: {ex.StackTrace}");

                if (revitFlaggedInvalid && !modelInfo.IsLocalFile)
                {
                    // Cloud model reported as missing/invalid — opening it crashes Revit.
                    // Flag unreachable so ProcessModel skips OpenDocumentFile and the retry queue can try again.
                    modelReachable = false;
                }
                else
                {
                    Log.Warn($"Defaulting workset configuration to {fallback}.");
                }
            }

            return config;
        }

        private bool SleepWithCountdown(int totalMs, BatchProgressForm progressForm, string statusPrefix)
        {
            Log.Info($"[Sleep] Start: {totalMs}ms, prefix='{statusPrefix}'.");
            var deadline = DateTime.UtcNow.AddMilliseconds(totalMs);
            int lastSecShown = -1;

            while (true)
            {
                if (progressForm.AbortRequested) { Log.Info("[Sleep] Aborted by progress form."); return false; }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining.TotalMilliseconds <= 0) { Log.Info("[Sleep] Completed normally."); return true; }

                int sec = (int)Math.Ceiling(remaining.TotalSeconds);
                if (sec != lastSecShown)
                {
                    // Per-second heartbeat — if Revit dies mid-sleep, the last second logged
                    // narrows the window. Cost: ~5 extra log lines per recovery pause.
                    Log.Info($"[Sleep] {sec}s remaining; about to DoEvents.");
                    progressForm.UpdateStatus($"{statusPrefix} {sec}s...");
                    lastSecShown = sec;
                }

                Thread.Sleep(100);
                Application.DoEvents();
            }
        }

        private void SuppressDialogs(object sender, DialogBoxShowingEventArgs e)
        {
            if (e.DialogId == "Dialog_Revit_DocWarnDialog")
            {
                // This dialog has element errors (e.g. "Cannot form radial dimension").
                // Buttons vary — e.g. "Delete Dimension(s)" | OK (greyed) | Cancel — and
                // the int ID for "proceed" is not knowable at compile time; guessing hasn't worked.
                // Strategy: don't suppress the dialog; let it appear, then click the right
                // button via Win32 from a background thread.
                Log.Info($"[SuppressDialogs] DocWarnDialog — letting dialog render; Win32 clicker thread starting.");
                Task.Run(() => ClickProceedButton());
                return; // do NOT call OverrideResult
            }

            try { Log.Info($"[SuppressDialogs] Cancel — DialogId='{e.DialogId}', Type={e.GetType().Name}"); }
            catch { }
            e.OverrideResult((int)DialogResult.Cancel);
        }

        // Polls for the DocWarnDialog native window and clicks the first enabled non-Cancel button.
        // Runs on a background thread while the dialog is blocking the Revit UI thread.
        private static void ClickProceedButton()
        {
            uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

            for (int tick = 0; tick < 100; tick++) // up to 10 s
            {
                Thread.Sleep(100);
                if (ClickButtonInDialog(pid, wantCancel: false)) return;
            }

            Log.Warn("[WinClick] 10 s timeout — clicking Cancel to unblock Revit.");
            ClickButtonInDialog(pid, wantCancel: true);
        }

        private static bool ClickButtonInDialog(uint pid, bool wantCancel)
        {
            bool clicked = false;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint wpid);
                if (wpid != pid) return true;

                // Skip our own WinForms windows (BatchProgressForm etc.) — they have
                // class names starting with "WindowsForms".
                var winClass = new StringBuilder(64);
                GetClassName(hwnd, winClass, 64);
                if (winClass.ToString().StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase)) return true;

                // Collect all enabled, visible child buttons.
                var candidates = new List<(IntPtr h, string t)>();
                EnumChildWindows(hwnd, (child, __) =>
                {
                    var cc = new StringBuilder(64);
                    GetClassName(child, cc, 64);
                    if (!cc.ToString().Equals("Button", StringComparison.OrdinalIgnoreCase)) return true;
                    if (!IsWindowEnabled(child) || !IsWindowVisible(child)) return true;

                    var txt = new StringBuilder(256);
                    GetWindowText(child, txt, 256);
                    string t = txt.ToString().Trim();
                    if (!string.IsNullOrEmpty(t)) candidates.Add((child, t));
                    return true;
                }, IntPtr.Zero);

                if (candidates.Count == 0) return true;

                // Log what we found so the right filter is visible in the log.
                Log.Info($"[WinClick] Dialog class='{winClass}', buttons: {string.Join(", ", candidates.Select(b => $"'{b.t}'"))}");

                var target = wantCancel
                    ? candidates.FirstOrDefault(b => b.t.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                    : candidates.FirstOrDefault(b => b.t.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) < 0);

                if (target.h == IntPtr.Zero) return true;

                Log.Info($"[WinClick] Clicking '{target.t}'.");
                SendMessage(target.h, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                clicked = true;
                return false;
            }, IntPtr.Zero);

            return clicked;
        }

        private bool RecoverBetweenModels(BatchProgressForm progressForm)
        {
            // Per-step diagnostics: prior runs end the log at "Pausing + forcing GC" with no
            // following entries, so the crash is somewhere inside this method or the next
            // iteration's first call. Bracket every step so we can pinpoint it.
            Log.Info("[Recover] Pausing + forcing GC before next model to release Revit cloud handles...");
            try
            {
                Log.Info("[Recover] GC.Collect #1 starting...");
                GC.Collect();
                Log.Info("[Recover] GC.Collect #1 returned.");

                Log.Info("[Recover] GC.WaitForPendingFinalizers starting...");
                GC.WaitForPendingFinalizers();
                Log.Info("[Recover] GC.WaitForPendingFinalizers returned.");

                Log.Info("[Recover] GC.Collect #2 starting...");
                GC.Collect();
                Log.Info("[Recover] GC.Collect #2 returned.");
            }
            catch (Exception ex)
            {
                Log.Error($"[Recover] GC step threw: {ex.GetType().Name}: {ex.Message}");
            }

            Log.Info($"[Recover] Entering SleepWithCountdown({RecoverDelayMs}ms)...");
            var slept = SleepWithCountdown(RecoverDelayMs, progressForm, "Recovering before next model in");
            Log.Info($"[Recover] SleepWithCountdown returned {slept}.");
            return slept;
        }

        // Win32 — dialog button inspection for ClickProceedButton
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lp);
        private const uint BM_CLICK = 0x00F5;
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc fn, IntPtr lp);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // Win32 — process termination
        // Terminates the current process with a specific exit code, bypassing CLR shutdown
        // hooks entirely. This avoids plugin unload dialogs (e.g. Rhino.Inside) that would
        // block Environment.Exit() or a normal Revit close.
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

        private void CleanupAndExit(bool killRevit, int exitCode = 0)
        {
            // Only delete batch_task.json when the batch is genuinely finished. Non-zero
            // exit means "more work pending, scheduler please restart" — the file must
            // survive so the next Revit launch can resume.
            bool batchComplete = exitCode == 0 && (_instruction?.ModelsToProcess?.Count ?? 0) == 0;
            if (batchComplete)
            {
                try
                {
                    if (File.Exists(_taskFilePath)) File.Delete(_taskFilePath);
                    Log.Info("[Exit] Batch complete — removed batch_task.json.");
                }
                catch (Exception ex) { Log.Warn($"[Exit] Failed to delete task file: {ex.Message}"); }
            }
            else
            {
                Log.Info($"[Exit] Leaving batch_task.json in place ({_instruction?.ModelsToProcess?.Count ?? 0} model(s) pending).");
            }

            if (killRevit)
            {
                Log.Info($"[Exit] Terminating Revit process with exit code {exitCode}.");

                // TerminateProcess sets the requested exit code on the process. Task
                // Scheduler can be configured to retry on non-zero exits, which is how
                // the crash-survival loop works: code 1 = "restart me", code 0 = "done".
                TerminateProcess(GetCurrentProcess(), (uint)exitCode);
            }
        }
    }
}