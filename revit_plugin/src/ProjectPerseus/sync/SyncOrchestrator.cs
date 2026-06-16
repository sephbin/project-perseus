using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.auth;
using ProjectPerseus.config;
using ProjectPerseus.models;
using ProjectPerseus.queue;
using ProjectPerseus.revit;
using ProjectPerseus.ui;
using ProjectPerseus.web;

using ProjectPerseus.logging;
using ProjectPerseus.util;
namespace ProjectPerseus.sync
{
    // Stateful coordinator for the Revit sync lifecycle. Owns the pre-sync queue check,
    // post-sync server callbacks, and ProgressChanged-based early-release fallback.
    // Plugin.cs constructs one in OnStartup and forwards Subscribe/Unsubscribe.
    //
    // Static IsAutoSyncing / AutoSyncExternalEvent are exposed so the AutoSyncEvent
    // handler (which Revit invokes on the main thread) can flip the flag and so the
    // QueuePoller background task can raise the event without holding an instance reference.
    public class SyncOrchestrator
    {
        public static ExternalEvent AutoSyncExternalEvent { get; private set; }
        public static bool IsAutoSyncing { get; set; } = false;

        private readonly Config _config = Config.Instance;
        private bool _isSyncing = false;
        private bool _queueReleasedEarly = false;
        private QueueWebForm _queueWebForm;
        private Document _currentSyncDoc = null;
        private string _currentSynCaption = "";
        private QueuePoller _autoSyncPoller;

        // Stage timing — reset at the start of each sync in doOnPriorToSync.
        // _stageTimeStart is set on the first ProgressChanged event (i.e. when Revit
        // actually starts work, after Perseus's pre-sync check has returned).
        private DateTime _stageTimeStart;
        private DateTime _stageTimeReloadLatest;
        private DateTime _stageTimeSaveToCentral;
        private DateTime _stageTimeFinalSaveLocal;
        private DateTime _stageTimeRevitComplete;
        private bool _stageReloadSeen;
        private bool _stageSaveCentralSeen;
        private bool _stageFinalLocalSeen;

        public void Subscribe(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizingWithCentral += OnDocumentSynchronizingWithCentral;
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.ProgressChanged += OnProgressChanged;

            var syncHandler = new AutoSyncEvent();
            AutoSyncExternalEvent = ExternalEvent.Create(syncHandler);
        }

        public void Unsubscribe(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizingWithCentral -= OnDocumentSynchronizingWithCentral;
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.ProgressChanged -= OnProgressChanged;
            _queueWebForm?.Close();
        }

        private void OnDocumentSynchronizingWithCentral(object sender, DocumentSynchronizingWithCentralEventArgs e)
        {
            doOnPriorToSync(e);
        }

        private void OnDocumentSynchronizedWithCentral(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            if (e.Status == RevitAPIEventStatus.Succeeded)
            {
                _stageTimeRevitComplete = DateTime.UtcNow;

                // Log the post-sync gap so it is visible in the log bracket.
                // This time includes Revit's own post-sync work plus any plugins whose
                // DocumentSynchronizedWithCentral handler was registered before ours.
                if (_stageFinalLocalSeen)
                    Log.Info($"Perseus handler entered — {FormatStageDuration(_stageTimeRevitComplete - _stageTimeFinalSaveLocal)} since queue release (Revit post-sync + other plugin handlers).");
                else
                    Log.Info("Perseus handler entered (no queue-release timestamp — stage gap unknown).");

                // If the ProgressChanged early-release didn't fire (e.g. localised Revit caption),
                // run the post-sync server call now as a fallback.
                if (!_queueReleasedEarly)
                {
                    doOnPostSync(e.Document);
                }

                doOnSync(e);
            }
            else
            {
                Log.Info($"Revit Sync was {e.Status}. Skipping Perseus upload.");
                _isSyncing = false;
            }
            _currentSyncDoc = null;
        }

        private void OnProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (!_isSyncing || _queueReleasedEarly) return;

            string caption = e.Caption ?? "";

            Log.Info($"Sync Caption Changed: {caption}");

            // T0: timestamp the first progress event so "First Save to Local" duration is
            // measured from when Revit starts work, not from Perseus's pre-sync check.
            if (_stageTimeStart == default)
                _stageTimeStart = DateTime.UtcNow;

            // "Save the active project back to the Central Model" appears twice during sync:
            //   1st entry transition = Reload Latest starts (First Save to Local just ended)
            //   2nd entry transition = Save to Central starts (Reload Latest just ended)
            // We detect the transition into this caption (not each individual event within it)
            // by comparing the current caption against _currentSynCaption (the previous caption).
            if (caption.Contains("Save the active project back to the Central Model") &&
                !_currentSynCaption.Contains("Save the active project back to the Central Model"))
            {
                if (!_stageReloadSeen)
                {
                    _stageTimeReloadLatest = DateTime.UtcNow;
                    _stageReloadSeen = true;
                }
                else if (!_stageSaveCentralSeen)
                {
                    _stageTimeSaveToCentral = DateTime.UtcNow;
                    _stageSaveCentralSeen = true;
                }
            }

            // Caption transition "Save to Central → Open an existing project" means the central
            // write has finished. Release the queue now rather than waiting for the full sync to
            // wrap up, so the next person isn't blocked unnecessarily.
            if (caption.Contains("Open an existing project") && _currentSynCaption.Contains("Save the active project back to the Central Model"))
            {
                _stageTimeFinalSaveLocal = DateTime.UtcNow;
                _stageFinalLocalSeen = true;

                _queueReleasedEarly = true;

                Log.Info("Detected 'Save to Local'. Releasing queue early via ProgressChanged event!");

                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (_currentSyncDoc != null)
                        {
                            doOnPostSync(_currentSyncDoc);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Early release failed: {ex.Message}");
                    }
                });
            }
            _currentSynCaption = caption;
        }

        private void OpenWebQueueLink(string docGuid)
        {
            try
            {
                var serverRoot = new Uri(_config.BaseUrl).GetLeftPart(UriPartial.Authority);
                var webQueueUrl = $"{serverRoot}/syncboat/app/{docGuid}/";

                var existing = _queueWebForm;
                if (existing != null && !existing.IsDisposed)
                {
                    try { existing.BeginInvoke(new Action(() => existing.Close())); }
                    catch { /* already gone */ }
                }

                // Exchange auth token for a short-lived signed SSO token. Chromium strips
                // Authorization headers from navigation requests, so we embed the token in the URL
                // instead and let token-login create the Django session.
                string startUrl = webQueueUrl;
                string authToken = AuthService.GetAuthTokenSafely();
                string authScheme = AuthService.GetAuthSchemeSafely();
                if (!string.IsNullOrEmpty(authToken))
                {
                    try
                    {
                        var ssoEndpoint = $"{_config.BaseUrl.TrimEnd('/')}/api/sso-token/";
                        var responseJson = WebHelper.Post(ssoEndpoint, authToken, "{}", authScheme);
                        var ssoObj = JObject.Parse(responseJson);
                        var ssoToken = ssoObj["sso_token"]?.ToString();
                        if (!string.IsNullOrEmpty(ssoToken))
                        {
                            var tokenLoginUrl = $"{_config.BaseUrl.TrimEnd('/')}/api/token-login/";
                            var encodedNext = Uri.EscapeDataString(new Uri(webQueueUrl).PathAndQuery);
                            var encodedSso = Uri.EscapeDataString(ssoToken);
                            startUrl = $"{tokenLoginUrl}?sso_token={encodedSso}&next={encodedNext}";
                            Log.Info("SSO token acquired — WebView2 will inherit MSAL session.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"SSO token exchange failed, falling back to direct navigation: {ex.Message}");
                    }
                }

                var revitHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                var thread = new System.Threading.Thread(() =>
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    var form = new QueueWebForm(startUrl, revitHandle);
                    _queueWebForm = form;
                    System.Windows.Forms.Application.Run(form);
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();

                Log.Info($"Opened web queue dialog: {webQueueUrl}");
            }
            catch (Exception ex)
            {
                Log.Info($"Failed to open web queue dialog: {ex.Message}");
            }
        }

        private void doOnPriorToSync(DocumentSynchronizingWithCentralEventArgs e)
        {
            try
            {
                _isSyncing = true;
                _queueReleasedEarly = false;
                _currentSyncDoc = e.Document;
                _stageTimeStart = default;
                _stageTimeRevitComplete = default;
                _stageReloadSeen = false;
                _stageSaveCentralSeen = false;
                _stageFinalLocalSeen = false;

                // AutoSyncEvent already passed the queue check — skip re-entry to avoid an infinite loop.
                if (IsAutoSyncing)
                {
                    Log.Info("Auto-Sync in progress – skipping queue check.");
                    return;
                }

                Log.Info($"Sync initiated for: {e.Document?.Title ?? "unknown document"}");

                if (UploadConfigIsValid() == false)
                {
                    Log.Info("Upload config invalid – skipping preliminary check.");
                    return;
                }

                var revit = new RevitFacade(e.Document);
                var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);
                var baseUrl = _config.BaseUrl;
                var app = e.Document.Application;
                string revitUsername = app.Username;
                string revitAccountId = app.LoginUserId;
                string windowsUsername = Environment.UserName;
                string machineName = Environment.MachineName;

                var serverRoot = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
                var queueEndpoint = $"{serverRoot}/syncboat/api/v2/source/{docGuid}/queue/";

                string queueResponseJson = WebHelper.Get(queueEndpoint, null, null);

                var queueStatus = JsonConvert.DeserializeObject<SyncQueueResponse>(queueResponseJson);

                List<string> usersInQueue = queueStatus?.Queue ?? new List<string>();
                int queueCount = usersInQueue.Count;

                bool shouldShowDialog = true;

                if (queueCount > 0)
                {
                    // Queue may return full UPNs (e.g. Andrew.Butler@cox.com.au) — compare on the local part only.
                    string StripDomain(string u) => u.Contains("@") ? u.Split('@')[0] : u;

                    if (StripDomain(usersInQueue[0]).Equals(windowsUsername, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Info($"Queue position: 1 of {queueCount}. Proceeding without alert.");
                        shouldShowDialog = false;
                    }
                    else
                    {
                        int position = usersInQueue.FindIndex(u => StripDomain(u).Equals(windowsUsername, StringComparison.OrdinalIgnoreCase)) + 1;
                        string posStr = position > 0 ? $"{position} of {queueCount}" : $"not in queue (total: {queueCount})";
                        Log.Warn($"Queue position: {posStr}. Showing alert.");
                    }
                }
                else
                {
                    Log.Info("No one in the sync queue. Proceeding with preliminary check.");
                    shouldShowDialog = false;
                }

                if (shouldShowDialog)
                {
                    string queueList = string.Join(Environment.NewLine, usersInQueue);

                    using (var form = new SyncWarningForm(queueCount, queueList))
                    {
                        form.ShowDialog();

                        switch (form.SelectedAction)
                        {
                            case SyncWarningForm.SyncAction.SyncAnyway:
                                Log.Info("User chose: Sync Anyway.");
                                break;

                            case SyncWarningForm.SyncAction.JoinQueue:
                                Log.Info("User chose: Join Queue.");
                                e.Cancel();
                                System.Threading.Tasks.Task.Run(() => RevitSyncDialogCloser.TryClose());
                                OpenWebQueueLink(docGuid);
                                return;

                            case SyncWarningForm.SyncAction.Cancel:
                                Log.Info("User chose: Cancel Sync.");
                                e.Cancel();
                                return;

                            case SyncWarningForm.SyncAction.JoinQueueAndAutoSync:
                                Log.Info("User chose: Join Queue & Auto-Sync.");
                                e.Cancel();
                                System.Threading.Tasks.Task.Run(() => RevitSyncDialogCloser.TryClose());
                                OpenWebQueueLink(docGuid);

                                // Capture locals — instance fields and Revit API are off-limits on a background thread.
                                string autoSyncDocGuid = docGuid;
                                string autoSyncBaseUrl = baseUrl;
                                string autoSyncUser = windowsUsername;

                                System.Threading.Tasks.Task.Run(() =>
                                {
                                    try
                                    {
                                        // Queue join uses Windows username directly — no MSAL/browser auth needed.
                                        // Consistent with getCurrentQueue which is also keyed on Windows username.
                                        var joinEndpoint = $"{new Uri(autoSyncBaseUrl).GetLeftPart(UriPartial.Authority)}/syncboat/api/v2/source/{autoSyncDocGuid}/join/";
                                        var joinPayload = JsonConvert.SerializeObject(new { username = autoSyncUser.ToLower() });
                                        WebHelper.Post(joinEndpoint, null, joinPayload);
                                        Log.Info($"Joined sync queue for {autoSyncDocGuid}.");

                                        if (_autoSyncPoller == null)
                                            _autoSyncPoller = new QueuePoller(AutoSyncExternalEvent);
                                        _autoSyncPoller.StartPolling(autoSyncDocGuid);
                                    }
                                    catch (Exception asyncEx)
                                    {
                                        Log.Error($"Auto-sync queue join failed: {asyncEx.Message}");
                                    }
                                });
                                return;
                        }
                    }
                }

                var payload = new
                {
                    documentGuid = docGuid,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    revitUser = revitUsername,
                    revitAccountId = revitAccountId,
                    windowsUser = windowsUsername,
                    machine = machineName
                };

                string jsonPayload = JsonConvert.SerializeObject(payload);

                var preSyncEndpoint = $"{baseUrl}/presync/{docGuid}";
                string response = WebHelper.Post(preSyncEndpoint, AuthService.GetAuthTokenSafely(), jsonPayload);

                Log.Info($"Preliminary sync request sent. Response: {response}");
            }
            catch (Exception ex)
            {
                Log.Error($"Error during preliminary sync: {ex}");
            }
        }

        private void doOnPostSync(Document doc)
        {
            try
            {
                Log.Info("Sync finished – contacting web server...");

                if (UploadConfigIsValid() == false)
                {
                    Log.Info("Upload config invalid – skipping preliminary check.");
                    return;
                }

                var revit = new RevitFacade(doc);
                var docGuid = ModelGuidStorage.GetOrCreate(doc);
                var baseUrl = _config.BaseUrl;

                var app = doc.Application;
                string revitUsername = app.Username;
                string revitAccountId = app.LoginUserId;
                string windowsUsername = Environment.UserName;
                string machineName = Environment.MachineName;

                var payload = new
                {
                    documentGuid = docGuid,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    revitUser = revitUsername,
                    revitAccountId = revitAccountId,
                    windowsUser = windowsUsername,
                    machine = machineName
                };

                string jsonPayload = JsonConvert.SerializeObject(payload);

                var preSyncEndpoint = $"{baseUrl}/postsync/{docGuid}";
                string response = WebHelper.Post(preSyncEndpoint, AuthService.GetAuthTokenSafely(), jsonPayload);
                Log.Info($"Post sync request sent. Response: {response}");

                var leaveServerRoot = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
                var leaveEndpoint = $"{leaveServerRoot}/syncboat/api/v2/source/{docGuid}/leave/";
                try
                {
                    string leaveToken = AuthService.GetAuthTokenSafely();
                    // Syncboat leave/ validates a Microsoft JWT (3-part dot structure).
                    // PAT (hex string) and sandbox sentinel have no dots and cause "Not enough segments".
                    // Only forward the token when it's actually a JWT.
                    bool isJwt = leaveToken != null && leaveToken.Split('.').Length >= 3;
                    string effectiveLeaveToken = isJwt ? leaveToken : null;
                    var leavePayload = JsonConvert.SerializeObject(new { username = windowsUsername.ToLower() });
                    WebHelper.Post(leaveEndpoint, effectiveLeaveToken, leavePayload);
                    Log.Info("Removed from sync queue.");
                }
                catch (Exception leaveEx)
                {
                    Log.Warn($"Failed to leave queue: {leaveEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error during post sync: {ex}");
            }
        }

        private void doOnSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            using (var sentry = new SentryContext())
            {
                try
                {
                    _isSyncing = false;
                    if (UploadConfigIsValid() == false)
                    {
                        Log.Warn("Upload config is not valid - skipping upload.");
                        return;
                    }

                    Log.Info("Start Watch");
                    var watch = System.Diagnostics.Stopwatch.StartNew();

                    string batchId = Guid.NewGuid().ToString();
                    Log.Info($"SyncBatch: {batchId}");

                    try
                    {
                        var revit = new RevitFacade(e.Document);

                        Log.Info("Before PerformIncrementalSync");
                        IncrementalSyncRunner.PerformIncrementalSync(revit, batchId);

                        _config.LastSyncVersionGuid = RevitFacade.GetDocumentVersionGuid(revit.Document);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(new Exception($"Error performing sync: {ex.Message}", ex));
                        Log.Info($"Error performing sync: {ex.Message}");
                    }

                    // Signal server that all element POSTs for this batch are done.
                    // Server will trigger post-sync validation once all background tasks complete.
                    try
                    {
                        var docGuid = ModelGuidStorage.GetOrCreate(e.Document);
                        var batchCloseEndpoint = $"{_config.BaseUrl}/batchclose/";
                        var batchClosePayload = Newtonsoft.Json.JsonConvert.SerializeObject(
                            new { batchId = batchId, documentGuid = docGuid });
                        WebHelper.Post(batchCloseEndpoint, AuthService.GetAuthTokenSafely(), batchClosePayload);
                        Log.Info($"SyncBatch closed: {batchId}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"batchClose call failed (non-fatal): {ex.Message}");
                    }

                    watch.Stop();
                    Log.Info("End Watch");
                    LogStageSummary(watch.Elapsed);
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                    Log.Info(ex.ToString());
                }
            }
        }

        private static string FormatStageDuration(TimeSpan ts)
        {
            return ts.TotalSeconds >= 0 ? ts.ToString(@"mm\:ss") : "--:--";
        }

        private void LogStageSummary(TimeSpan perseusTime)
        {
            try
            {
                if (_stageReloadSeen && _stageSaveCentralSeen)
                {
                    string firstLocal = FormatStageDuration(_stageTimeReloadLatest - _stageTimeStart);
                    string reload     = FormatStageDuration(_stageTimeSaveToCentral - _stageTimeReloadLatest);
                    string toCentral  = FormatStageDuration(
                        _stageFinalLocalSeen
                            ? _stageTimeFinalSaveLocal - _stageTimeSaveToCentral
                            : _stageTimeRevitComplete  - _stageTimeSaveToCentral);
                    string finalLocal = _stageFinalLocalSeen
                        ? FormatStageDuration(_stageTimeRevitComplete - _stageTimeFinalSaveLocal)
                        : "--:--";

                    Log.Info("--- Sync Stage Timings (mm:ss) ---");
                    Log.Info($"  First Save to Local          : {firstLocal}");
                    Log.Info($"  Reload Latest                : {reload}");
                    Log.Info($"  Save to Central              : {toCentral}");
                    Log.Info($"  Post-sync gap (other plugins): {finalLocal}");
                    Log.Info($"  Perseus processing           : {perseusTime:mm\\:ss}");
                }
                else if (_stageReloadSeen && _stageFinalLocalSeen)
                {
                    // Caption appeared only once — can't split Reload/Save-to-Central separately.
                    string firstLocal = FormatStageDuration(_stageTimeReloadLatest - _stageTimeStart);
                    string combined   = FormatStageDuration(_stageTimeFinalSaveLocal - _stageTimeReloadLatest);
                    string finalLocal = FormatStageDuration(_stageTimeRevitComplete - _stageTimeFinalSaveLocal);
                    Log.Info("--- Sync Stage Timings (mm:ss) — partial ---");
                    Log.Info($"  First Save to Local          : {firstLocal}");
                    Log.Info($"  Reload + Save to Central     : {combined}  (caption appeared once; expected twice)");
                    Log.Info($"  Post-sync gap (other plugins): {finalLocal}");
                    Log.Info($"  Perseus processing           : {perseusTime:mm\\:ss}");
                }
                else
                {
                    Log.Info($"Sync completed in {perseusTime:hh\\:mm\\:ss}. Stage captions not detected (reload:{_stageReloadSeen} central:{_stageSaveCentralSeen}) — check 'Sync Caption Changed' log lines to calibrate caption patterns.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Stage timing summary failed: {ex.Message}");
            }
        }

        private bool UploadConfigIsValid()
        {
            return _config.BaseUrl != null
                   && UrlUtils.IsValidUrl(_config.BaseUrl);
        }
    }
}
