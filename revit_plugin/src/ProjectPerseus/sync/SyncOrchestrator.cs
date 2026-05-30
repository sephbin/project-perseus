using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.auth;
using ProjectPerseus.forms;
using ProjectPerseus.models;
using ProjectPerseus.queue;
using ProjectPerseus.revit;

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
                Utl.WriteLog($"Revit Sync was {e.Status}. Skipping Perseus upload.");
                _isSyncing = false;
            }
            _currentSyncDoc = null;
        }

        private void OnProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (!_isSyncing || _queueReleasedEarly) return;

            string caption = e.Caption ?? "";

            Utl.WriteLog($"Sync Caption Changed: {caption}");

            // Caption transition "Save to Central → Open an existing project" means the central
            // write has finished. Release the queue now rather than waiting for the full sync to
            // wrap up, so the next person isn't blocked unnecessarily.
            if (caption.Contains("Open an existing project") && _currentSynCaption.Contains("Save the active project back to the Central Model"))
            {
                _queueReleasedEarly = true;

                Utl.WriteLog("Detected 'Save to Local'. Releasing queue early via ProgressChanged event!");

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
                        Utl.WriteLog($"Early release failed: {ex.Message}");
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

                // Exchange MSAL Bearer token for a short-lived signed SSO token. Chromium strips
                // Authorization headers from navigation requests, so we embed the token in the URL
                // instead and let token-login create the Django session.
                string startUrl = webQueueUrl;
                string msalToken = AuthService.GetAuthTokenSafely();
                if (!string.IsNullOrEmpty(msalToken))
                {
                    try
                    {
                        var ssoEndpoint = $"{_config.BaseUrl.TrimEnd('/')}/api/sso-token/";
                        var responseJson = Utl.WebHelper.Post(ssoEndpoint, msalToken, "{}");
                        var ssoObj = JObject.Parse(responseJson);
                        var ssoToken = ssoObj["sso_token"]?.ToString();
                        if (!string.IsNullOrEmpty(ssoToken))
                        {
                            var tokenLoginUrl = $"{_config.BaseUrl.TrimEnd('/')}/api/token-login/";
                            var encodedNext = Uri.EscapeDataString(new Uri(webQueueUrl).PathAndQuery);
                            var encodedSso = Uri.EscapeDataString(ssoToken);
                            startUrl = $"{tokenLoginUrl}?sso_token={encodedSso}&next={encodedNext}";
                            Utl.WriteLog("SSO token acquired — WebView2 will inherit MSAL session.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Utl.WriteLog($"SSO token exchange failed, falling back to direct navigation: {ex.Message}", LogLevel.Warn);
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

                Utl.WriteLog($"Opened web queue dialog: {webQueueUrl}");
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Failed to open web queue dialog: {ex.Message}");
            }
        }

        private void doOnPriorToSync(DocumentSynchronizingWithCentralEventArgs e)
        {
            try
            {
                _isSyncing = true;
                _queueReleasedEarly = false;
                _currentSyncDoc = e.Document;

                // AutoSyncEvent already passed the queue check — skip re-entry to avoid an infinite loop.
                if (IsAutoSyncing)
                {
                    Utl.WriteLog("Auto-Sync in progress – skipping queue check.");
                    return;
                }

                Utl.WriteLog($"Sync initiated for: {e.Document?.Title ?? "unknown document"}");

                if (UploadConfigIsValid() == false)
                {
                    Utl.WriteLog("Upload config invalid – skipping preliminary check.");
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

                var queueEndpoint = $"{baseUrl}/../syncboat/api/v2/source/{docGuid}/queue/";

                string queueResponseJson = Utl.WebHelper.Get(queueEndpoint, null, null);

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
                        Utl.WriteLog($"Queue position: 1 of {queueCount}. Proceeding without alert.");
                        shouldShowDialog = false;
                    }
                    else
                    {
                        int position = usersInQueue.FindIndex(u => StripDomain(u).Equals(windowsUsername, StringComparison.OrdinalIgnoreCase)) + 1;
                        string posStr = position > 0 ? $"{position} of {queueCount}" : $"not in queue (total: {queueCount})";
                        Utl.WriteLog($"Queue position: {posStr}. Showing alert.", LogLevel.Warn);
                    }
                }
                else
                {
                    Utl.WriteLog("No one in the sync queue. Proceeding with preliminary check.");
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
                                Utl.WriteLog("User chose: Sync Anyway.");
                                break;

                            case SyncWarningForm.SyncAction.JoinQueue:
                                Utl.WriteLog("User chose: Join Queue.");
                                e.Cancel();
                                System.Threading.Tasks.Task.Run(() => RevitSyncDialogCloser.TryClose());
                                OpenWebQueueLink(docGuid);
                                return;

                            case SyncWarningForm.SyncAction.Cancel:
                                Utl.WriteLog("User chose: Cancel Sync.");
                                e.Cancel();
                                return;

                            case SyncWarningForm.SyncAction.JoinQueueAndAutoSync:
                                Utl.WriteLog("User chose: Join Queue & Auto-Sync.");
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
                                        var joinEndpoint = $"{autoSyncBaseUrl}/../syncboat/api/v2/source/{autoSyncDocGuid}/join/";
                                        var joinPayload = JsonConvert.SerializeObject(new { username = autoSyncUser.ToLower() });
                                        Utl.WebHelper.Post(joinEndpoint, null, joinPayload);
                                        Utl.WriteLog($"Joined sync queue for {autoSyncDocGuid}.");

                                        if (_autoSyncPoller == null)
                                            _autoSyncPoller = new QueuePoller(AutoSyncExternalEvent);
                                        _autoSyncPoller.StartPolling(autoSyncDocGuid);
                                    }
                                    catch (Exception asyncEx)
                                    {
                                        Utl.WriteLog($"Auto-sync queue join failed: {asyncEx.Message}", LogLevel.Error);
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
                string response = Utl.WebHelper.Post(preSyncEndpoint, AuthService.GetAuthTokenSafely(), jsonPayload);

                Utl.WriteLog($"Preliminary sync request sent. Response: {response}");
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Error during preliminary sync: {ex}", LogLevel.Error);
            }
        }

        private void doOnPostSync(Document doc)
        {
            try
            {
                Utl.WriteLog("Sync finished – contacting web server...");

                if (UploadConfigIsValid() == false)
                {
                    Utl.WriteLog("Upload config invalid – skipping preliminary check.");
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
                string response = Utl.WebHelper.Post(preSyncEndpoint, AuthService.GetAuthTokenSafely(), jsonPayload);
                Utl.WriteLog($"Post sync request sent. Response: {response}");

                // Use the MSAL token to leave the queue so request.user on the server matches
                // whoever actually authenticated.
                string leaveToken = AuthService.GetAuthTokenSafely();
                if (!string.IsNullOrEmpty(leaveToken))
                {
                    var leaveEndpoint = $"{baseUrl.TrimEnd('/')}/../syncboat/api/v2/source/{docGuid}/leave/";
                    try
                    {
                        Utl.WebHelper.Post(leaveEndpoint, leaveToken, "{}");
                        Utl.WriteLog("Removed from sync queue.");
                    }
                    catch (Exception leaveEx)
                    {
                        Utl.WriteLog($"Failed to leave queue: {leaveEx.Message}", LogLevel.Warn);
                    }
                }
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Error during post sync: {ex}", LogLevel.Error);
            }
        }

        private void doOnSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            using (var sentry = new Utl.SentryContext())
            {
                try
                {
                    _isSyncing = false;
                    if (UploadConfigIsValid() == false)
                    {
                        Log.Warn("Upload config is not valid - skipping upload.");
                        return;
                    }

                    Utl.WriteLog("Start Watch");
                    var watch = System.Diagnostics.Stopwatch.StartNew();

                    try
                    {
                        var revit = new RevitFacade(e.Document);

                        Utl.WriteLog("Before PerformIncrementalSync");
                        IncrementalSyncRunner.PerformIncrementalSync(revit);

                        _config.LastSyncVersionGuid = RevitFacade.GetDocumentVersionGuid(revit.Document);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(new Exception($"Error performing sync: {ex.Message}", ex));
                        Utl.WriteLog($"Error performing sync: {ex.Message}");
                    }

                    watch.Stop();
                    Utl.WriteLog("End Watch");
                    Utl.WriteLog($"Sync completed in {watch.Elapsed:hh\\:mm\\:ss}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                    Utl.WriteLog(ex.ToString());
                }
            }
        }

        private bool UploadConfigIsValid()
        {
            return _config.BaseUrl != null
                   && Utl.IsValidUrl(_config.BaseUrl);
        }
    }
}
