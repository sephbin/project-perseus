using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.ui;
using ProjectPerseus.forms;
using ProjectPerseus.auth;
using System.IO;
using static System.Net.Mime.MediaTypeNames;
using System.Reflection;
using System.Windows.Media.Imaging;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;
using System.Reflection.PortableExecutable;
using System.Linq;


namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    

    public class Plugin : IExternalApplication
    {   
        private readonly Config _config = Config.Instance;
        public static Autodesk.Revit.UI.ExternalEvent AutoSyncExternalEvent { get; private set; }
        public static bool IsAutoSyncing { get; set; } = false;
        private queue.QueuePoller _autoSyncPoller;
        private bool _isSyncing = false;
        private bool _queueReleasedEarly = false;
        private QueueWebForm _queueWebForm;
        private Document _currentSyncDoc = null;
        private string _currentSynCaption = "";
        private System.Diagnostics.Stopwatch _startupStopwatch;
        private string _batchFilePath;

        //This adds the "OnDocumentSynchronizedWithCentral" function to the "DocumentSynchronizedWithCentral" event stack
        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizingWithCentral += OnDocumentSynchronizingWithCentral;
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            AddRibbonPanel(application);
            ThemeIconManager.Initialize(application);
            string revitVersion = application.ControlledApplication.VersionNumber;
            string pluginVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            Utl.InitSession(revitVersion, pluginVersion);

            var syncHandler = new Commands.AutoSyncEvent();
            AutoSyncExternalEvent = Autodesk.Revit.UI.ExternalEvent.Create(syncHandler);
            application.ControlledApplication.ProgressChanged += OnProgressChanged;

            /// BATCH TRIGGER LOGIC ///
            string roamingFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _batchFilePath = Path.Combine(roamingFolder, "ProjectPerseus", "batch_task.json");

            if (File.Exists(_batchFilePath))
            {
                Utl.WriteLog("Batch task file detected. Hooking into Idling event with Stopwatch...");

                // 2. Start the stopwatch and hook into Idling
                _startupStopwatch = System.Diagnostics.Stopwatch.StartNew();
                application.Idling += OnRevitIdlingDelay;
            }
            /// END BATCH TRIGGER LOGIC ///

            return Result.Succeeded;
        }

        //This is a wrapper for doOnPriorToSync, as it doesn't require as many arguments
        private void OnDocumentSynchronizingWithCentral(object sender, DocumentSynchronizingWithCentralEventArgs e)
        {
            doOnPriorToSync(e);
             
        }
        private void OpenWebQueueLink(string docGuid)
        {
            try
            {
                var serverRoot = new Uri(_config.BaseUrl).GetLeftPart(UriPartial.Authority);
                var webQueueUrl = $"{serverRoot}/syncboat/app/{docGuid}/";

                // Close any existing form (cross-thread safe)
                var existing = _queueWebForm;
                if (existing != null && !existing.IsDisposed)
                {
                    try { existing.BeginInvoke(new Action(() => existing.Close())); }
                    catch { /* already gone */ }
                }

                // Exchange MSAL Bearer token for a short-lived signed SSO token.
                // Chromium strips Authorization headers from navigation requests, so we embed
                // the token in the URL instead and let token-login create the Django session.
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
                            var encodedSso  = Uri.EscapeDataString(ssoToken);
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

                // Skip queue check when Perseus itself triggered the sync via AutoSyncEvent
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
                string revitUsername = app.Username;        // Name from Revit Options
                string revitAccountId = app.LoginUserId;    // Autodesk account GUID (if logged in)
                string windowsUsername = Environment.UserName;
                string machineName = Environment.MachineName;


                // --- 1. Check Sync Queue Status ---
                var queueEndpoint = $"{baseUrl}/../syncboat/api/v2/source/{docGuid}/queue/";

                string queueResponseJson = Utl.WebHelper.Get(queueEndpoint, null, null);

                // Deserialize the response into the new model structure
                var queueStatus = JsonConvert.DeserializeObject<SyncQueueResponse>(queueResponseJson);

                // Get the list of users and the count
                List<string> usersInQueue = queueStatus?.Queue ?? new List<string>();
                int queueCount = usersInQueue.Count;

                // --- 2. Prompt User if Queue Exists ---
                // --- 2. Conditional Dialog Logic ---

                bool shouldShowDialog = true;

                if (queueCount > 0)
                {
                    // Queue may return full UPNs (e.g. Andrew.Butler@cox.com.au) — compare on the local part only
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
                    // No one is in the queue. No dialog needed.
                    Utl.WriteLog("No one in the sync queue. Proceeding with preliminary check.");
                    shouldShowDialog = false;
                }


                // --- 3. Prompt User ---
                if (shouldShowDialog)
                {
                    string queueList = string.Join(Environment.NewLine, usersInQueue);

                    using (var form = new SyncWarningForm(queueCount, queueList))
                    {
                        form.ShowDialog();

                        // SWITCH ON USER CHOICE
                        switch (form.SelectedAction)
                        {
                            case SyncWarningForm.SyncAction.SyncAnyway:
                                Utl.WriteLog("User chose: Sync Anyway.");
                                // Do nothing here, let the code flow proceed below.
                                break;

                            case SyncWarningForm.SyncAction.JoinQueue:
                                Utl.WriteLog("User chose: Join Queue.");
                                e.Cancel();
                                System.Threading.Tasks.Task.Run(() => TryCloseRevitSyncDialog());
                                OpenWebQueueLink(docGuid);
                                return;

                            case SyncWarningForm.SyncAction.Cancel:
                                Utl.WriteLog("User chose: Cancel Sync.");
                                e.Cancel(); // Stop Revit Sync
                                return; // Exit function
                            case SyncWarningForm.SyncAction.JoinQueueAndAutoSync:
                                Utl.WriteLog("User chose: Join Queue & Auto-Sync.");
                                e.Cancel();
                                System.Threading.Tasks.Task.Run(() => TryCloseRevitSyncDialog());
                                OpenWebQueueLink(docGuid);

                                // Capture locals — instance fields and Revit API are off-limits on a background thread.
                                string autoSyncDocGuid = docGuid;
                                string autoSyncBaseUrl  = baseUrl;
                                string autoSyncUser     = windowsUsername;

                                System.Threading.Tasks.Task.Run(() =>
                                {
                                    try
                                    {
                                        // Queue join uses Windows username directly — no MSAL/browser auth needed.
                                        // Consistent with getCurrentQueue which is also keyed on Windows username.
                                        var joinEndpoint = $"{autoSyncBaseUrl}/../syncboat/api/v2/source/{autoSyncDocGuid}/join/";
                                        var joinPayload = Newtonsoft.Json.JsonConvert.SerializeObject(new { username = autoSyncUser.ToLower() });
                                        Utl.WebHelper.Post(joinEndpoint, null, joinPayload);
                                        Utl.WriteLog($"Joined sync queue for {autoSyncDocGuid}.");

                                        if (_autoSyncPoller == null)
                                            _autoSyncPoller = new queue.QueuePoller(AutoSyncExternalEvent);
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


                // --- 4. Continue with Pre-Sync Payload (Only if SyncAnyway or No Dialog) ---

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

                // Preliminary call to the web API to verify or register sync start
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
                    string revitUsername = app.Username;        // Name from Revit Options
                    string revitAccountId = app.LoginUserId;    // Autodesk account GUID (if logged in)
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

                    // Log sync end event in Perseus
                    var preSyncEndpoint = $"{baseUrl}/postsync/{docGuid}";
                    string response = Utl.WebHelper.Post(preSyncEndpoint, AuthService.GetAuthTokenSafely(), jsonPayload);
                    Utl.WriteLog($"Post sync request sent. Response: {response}");

                    // Remove the authenticated user from the syncboat queue using the MSAL token
                    // so request.user on the server matches whoever actually authenticated.
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
        //This appears to be a wrapper for the doOnSync function so it doesn't need as many arguments
        private void OnDocumentSynchronizedWithCentral(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            if (e.Status == RevitAPIEventStatus.Succeeded)
            {
                // 🔹 NEW: If the progress hack failed to fire (e.g. language difference), 
                // fire it now as a fallback to ensure the queue is released.
                if (!_queueReleasedEarly)
                {
                    doOnPostSync(e.Document);
                }

                doOnSync(e);
            }
            else
            {
                // If it failed or was cancelled, we log it but DO NOT send data to Django
                Utl.WriteLog($"Revit Sync was {e.Status}. Skipping Perseus upload.");
                _isSyncing = false;
            }
            _currentSyncDoc = null;
        }

        //Decides what type of sync to do
        private void doOnSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            //WriteLog("doOnSync");
            using (var sentry = new Utl.SentryContext())
            {
                try
                {
                    _isSyncing = false;
                    if (UploadConfigIsValid() == false)
                    {
                        Log.Warn("Upload config is not valid - skipping upload.");
                        //Utl.WriteLog("Upload config is not valid - skipping upload.");
                        return;
                    }

                    // record elapsed time
                    Utl.WriteLog("Start Watch");
                    var watch = System.Diagnostics.Stopwatch.StartNew();

                    try
                    {
                        var revit = new RevitFacade(e.Document);

                        Utl.WriteLog("Before PerformIncrementalSync");
                        PerformIncrementalSync(revit);

                        //if (Config.Instance.FullSyncNextSync)
                        //{
                        //    Log.Info("Full sync requested - uploading all elements...");
                        //    WriteLog("Full sync requested - uploading all elements...");
                        //    Config.Instance.FullSyncNextSync = false;

                        //    PerformFullSync(revit);
                        //}
                        //else
                        //{
                        //    Log.Info("Incremental sync requested - uploading changed elements...");
                        //    WriteLog("Incremental sync requested - uploading changed elements...");
                        //    PerformIncrementalSync(revit);
                        //}
                        //WriteLog("Before _config.LastSyncVersionGuid");
                        _config.LastSyncVersionGuid = RevitFacade.GetDocumentVersionGuid(revit.Document);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(new Exception($"Error performing sync: {ex.Message}", ex));
                        Utl.WriteLog($"Error performing sync: {ex.Message}");

                    }

                    watch.Stop();
                    Utl.WriteLog("End Watch");
                    //Log.Info($"Sync completed in {watch.Elapsed:hh\\:mm\\:ss}");
                    Utl.WriteLog($"Sync completed in {watch.Elapsed:hh\\:mm\\:ss}");
                    // dump json
                    // Utl.JsonDump(elements, "ElementList");
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                    Utl.WriteLog(ex.ToString());
                }
            }
            //WriteLog("  //doOnSync");
        }

        public static void PerformFullSync(RevitFacade revit)
        {

            //Create a Perseus Source and Project Set
            try
            {
                Utl.WriteLog("Start Watch");
                var watch = System.Diagnostics.Stopwatch.StartNew();

                // Create a Perseus Source and Project Set
                var doc = revit.Document;
                var app = doc.Application;
                var thisdocGuid = ModelGuidStorage.GetOrCreate(doc);
                var baseUrl = Config.Instance.BaseUrl;

                // --- Collect metadata about Revit model ---
                string fileName = doc.Title;
                string filePath = doc.PathName;
                string revitVersion = app.VersionNumber;
                //string revitBuild = app.Build;
                //string username = app.Username;
                //string accountId = app.LoginUserId ?? "N/A";
                //string windowsUser = Environment.UserName;
                //string machine = Environment.MachineName;

                string projectNumber = "";
                string projectName = "";
                string clientName = "";

                try
                {
                    var projInfo = doc.ProjectInformation;
                    if (projInfo != null)
                    {
                        projectNumber = projInfo.LookupParameter("Project Number")?.AsString() ?? "";
                        projectName = projInfo.LookupParameter("Project Name")?.AsString() ?? "";
                        clientName = projInfo.LookupParameter("Client Name")?.AsString() ?? "";
                    }
                }
                catch (Exception ex)
                {
                    Utl.WriteLog($"Failed to read project info: {ex.Message}");
                }

                // --- Package everything into a metadata payload ---
                var metadata = new
                {
                    documentGuid = thisdocGuid,
                    fileName = fileName,
                    filePath = filePath,
                    revitVersion = revitVersion,
                    //revitBuild = revitBuild,
                    //revitUser = username,
                    //revitAccountId = accountId,
                    //windowsUser = windowsUser,
                    //machine = machine,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    projectInfo = new
                    {
                        number = projectNumber,
                        name = projectName,
                        client = clientName
                    }
                };

                // --- Send to server ---
                var metadataEndpoint = $"{baseUrl}/registersource/";
                string jsonMetadata = JsonConvert.SerializeObject(metadata);
                string response = Utl.WebHelper.Post(metadataEndpoint, AuthService.GetAuthTokenSafely(), jsonMetadata);
                JObject json = JObject.Parse(response);
                Utl.WriteLog($"Metadata upload response: {response}");
            


                var elements = revit.GetAllElements();
                Utl.WriteLog($"PerformFullSync: Found {elements.Count} elements");

                var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);
                Utl.WriteLog(docGuid);

                var elementDeltaList = ElementDelta.CreateList(ElementDelta.DeltaAction.Create, elements, revit.Document, docGuid).ToList();
                Utl.WriteLog("PerformFullSync: Created elementDeltaList");
               
                var filteredElementDeltaList = new List<ElementDelta>();

                var categories = json["source"]["parameter_dict"]["perseusCategories"].ToObject<List<string>>();

                try { filteredElementDeltaList = elementDeltaList.FilterByCategoryName(categories).ToList(); }
                catch (Exception ex) { Utl.WriteLog(ex.ToString()); }

                try
                {
                    Utl.WriteLog("Harvesting Categories...");
                    var categoryDeltas = new List<ElementDelta>();

                    foreach (Category cat in revit.Document.Settings.Categories)
                    {
                        // Optional: Filter out weird categories if you want
                        // if (cat.CategoryType == CategoryType.Invalid) continue;

                        // Wrap the Category in our new Adapter
                        var catAdapter = new ProjectPerseus.revit.adapters.ArdbCategoryAdapter(cat);

                        // Create a Delta for it (Treat it as an Update/Create)
                        var delta = new ElementDelta(ElementDelta.DeltaAction.Update, catAdapter, revit.Document, docGuid);

                        categoryDeltas.Add(delta);
                    }

                    Utl.WriteLog($"Added {categoryDeltas.Count} Categories to the payload.");

                    // Add them to the final list
                    filteredElementDeltaList.AddRange(categoryDeltas);
                }
                catch (Exception ex)
                {
                    Utl.WriteLog($"Error harvesting categories: {ex.Message}");
                }

                // Collect Connected Elements
                try
                {
                    // Check the boolean flag from the JSON response 
                    bool collectConnected = false;
                    if (json["source"]?["parameter_dict"]?["perseusOption_collectConnectedElements"] != null)
                    {
                        collectConnected = (bool)json["source"]["parameter_dict"]["perseusOption_collectConnectedElements"];
                    }

                    if (collectConnected)
                    {
                        Utl.WriteLog("Option 'collectConnectedElements' is TRUE. Harvesting references...");

                        // 1. Harvest IDs from the Primary List
                        HashSet<long> referencedIds = ElementDelta.GetReferencedIds(filteredElementDeltaList);

                        // Remove IDs that are ALREADY in the Primary List (prevent duplicates/overwriting)
                        // Map the current delta list to IDs to check against
                        var existingIds = filteredElementDeltaList.Select(x => x.Element.Id).ToHashSet();

                        // Only keep IDs that we aren't already uploading
                        referencedIds.ExceptWith(existingIds);

                        Utl.WriteLog($"Found {referencedIds.Count} additional connected elements.");

                        if (referencedIds.Count > 0)
                        {
                            // Fetch the actual Element objects for these IDs
                            var connectedDeltas = ElementDelta.CreateListFromIds(referencedIds, revit.Document, docGuid);

                            // Add them to the main list
                            filteredElementDeltaList.AddRange(connectedDeltas);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Utl.WriteLog($"Error in CollectConnectedElements logic: {ex.Message}");
                }


                Utl.WriteLog("PerformFullSync: Filtered Element Delta List");
                SubmitElementState(filteredElementDeltaList);
                
                watch.Stop();
                Utl.WriteLog("End Watch");
                //Log.Info($"Sync completed in {watch.Elapsed:hh\\:mm\\:ss}");
                Utl.WriteLog($"Full Upload completed in {watch.Elapsed:hh\\:mm\\:ss}");
            }
            catch (Exception ex) { Utl.WriteLog(ex.ToString()); }
        }

        public static void PerformIncrementalSync(RevitFacade revit)
        {
            try
            {
                var _baseUrl = Config.Instance.BaseUrl;
                var docId = ModelGuidStorage.GetOrCreate(revit.Document);
                Utl.WriteLog(docId);
                var StateEndpoint = $"{_baseUrl}/getstate/{docId}";

                string stateJson = Utl.WebHelper.Get(StateEndpoint, null, null);
                JObject json = JObject.Parse(stateJson);

                var lastSyncVersionGuid = Guid.Parse(json["value"].ToString());
                Utl.WriteLog(lastSyncVersionGuid.ToString());

                var elementChangeSet = revit.GetElementChangeSet(lastSyncVersionGuid);

                if (elementChangeSet.ContainsChanges())
                {
                    var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);

                    // Create the Primary List
                    var elementDeltaList = ElementDelta.CreateListFromChangeSet(elementChangeSet, revit.Document, docGuid);
                    var categories = json["source"]["parameter_dict"]["perseusCategories"].ToObject<List<string>>();
                    elementDeltaList = elementDeltaList.FilterByCategoryName(categories);

                    // Collect Connected Elements
                    try
                    {
                        // Check the boolean flag from the JSON response 
                        bool collectConnected = false;
                        if (json["source"]?["parameter_dict"]?["perseusOption_collectConnectedElements"] != null)
                        {
                            collectConnected = (bool)json["source"]["parameter_dict"]["perseusOption_collectConnectedElements"];
                        }

                        if (collectConnected)
                        {
                            Utl.WriteLog("Option 'collectConnectedElements' is TRUE. Harvesting references...");

                            HashSet<long> referencedIds = ElementDelta.GetReferencedIds(elementDeltaList);
                            var existingIds = elementDeltaList.Select(x => x.Element.Id).ToHashSet();
                            referencedIds.ExceptWith(existingIds);

                            Utl.WriteLog($"Found {referencedIds.Count} additional connected elements.");

                            if (referencedIds.Count > 0)
                            {
                                var connectedDeltas = ElementDelta.CreateListFromIds(referencedIds, revit.Document, docGuid);
                                elementDeltaList.AddRange(connectedDeltas);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Utl.WriteLog($"Error in CollectConnectedElements logic: {ex.Message}");
                    }

                    var elementDeltaDeletedList = ElementDelta.CreateDeletedListFromChangeSet(elementChangeSet);

                    Utl.WriteLog("About to run SubmitElementDeltas");
                    SubmitElementDeltas(elementDeltaList, elementDeltaDeletedList, revit.Document);
                }
                else
                {
                    Log.Info("No changes detected - skipping upload.");
                    Utl.WriteLog("No changes detected - skipping upload.");
                }
            }
            // 🔹 2. The Fallback sits securely on the OUTER catch block
            catch (Autodesk.Revit.Exceptions.ArgumentException ex) when (ex.Message.Contains("baseVersionGUID"))
            {
                Utl.WriteLog("WARNING: Local incremental history is missing or broken (PacCache likely cleared).");
                Utl.WriteLog("Automatically falling back to PerformFullSync...");

                // 🔹 3. Fixed the variable name to 'revit'
                PerformFullSync(revit);
            }
            catch (Exception ex)
            {
                // Catch any other actual errors for the entire incremental process
                Utl.WriteLog($"PerformIncrementalSync critically failed: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        private static void SubmitElementDeltas(IList<ElementDelta> elements, IList<long> deleted, Document doc)
        {
            new ProjectPerseusWeb(Config.Instance.BaseUrl, Config.Instance.ApiToken).SubmitElementDeltas(elements, deleted, doc);
        }
        public static void SubmitElementState(IList<ElementDelta> elements)
        {
            new ProjectPerseusWeb(Config.Instance.BaseUrl, Config.Instance.ApiToken).SubmitElementState(elements);
        }
        private bool UploadConfigIsValid()
        {
            return _config.BaseUrl != null
                   && Utl.IsValidUrl(_config.BaseUrl);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
            ThemeIconManager.Shutdown(application);
            _queueWebForm?.Close();
            return Result.Succeeded;
        }
        
        
        static void AddRibbonPanel(UIControlledApplication application)
        {
            // Create a custom ribbon tab
            String tabName = "Perseus";
            application.CreateRibbonTab(tabName);

            // Add a new ribbon panel
            RibbonPanel ribbonPanel = application.CreateRibbonPanel(tabName, "Tools");

            // Get dll assembly path
            string thisAssemblyPath = Assembly.GetExecutingAssembly().Location;

            // create push button for CurveTotalLength
            PushButtonData b1Data = new PushButtonData(
                "Button_RunFullSync",
                "Upload",
                thisAssemblyPath,
                "ProjectPerseus.Commands.PerformFullUploadCommand");

            PushButton pb1 = ribbonPanel.AddItem(b1Data) as PushButton;
            pb1.ToolTip = "Upload all elements to external database";
            ThemeIconManager.Register(pb1, "perseus");

            PushButtonData settingsBtnData = new PushButtonData(
            "Button_Settings",
            "Settings",
            thisAssemblyPath,
            "ProjectPerseus.Commands.OpenSettingsCommand");

            PushButton settingsBtn = ribbonPanel.AddItem(settingsBtnData) as PushButton;
            settingsBtn.ToolTip = "Change Perseus Settings like: API Token and Upload URL";
            ThemeIconManager.Register(settingsBtn, "settings");

            PushButtonData resetBtnData = new PushButtonData(
                "Button_ResetGuid",
                "Reset Identity",
                thisAssemblyPath,
                "ProjectPerseus.Commands.ResetModelGuidCommand");

            PushButton resetBtn = ribbonPanel.AddItem(resetBtnData) as PushButton;
            resetBtn.ToolTip = "Generates a new Database GUID for this model. Use ONLY if this file was copied from an older project.";
            ThemeIconManager.Register(resetBtn, "reset");
        }

        private void OnProgressChanged(object sender, Autodesk.Revit.DB.Events.ProgressChangedEventArgs e)
        {
            // Only care if we are currently in a Sync operation and haven't released the queue yet
            if (!_isSyncing || _queueReleasedEarly) return;

            // e.Caption contains the text shown next to the progress bar
            string caption = e.Caption ?? "";

            Utl.WriteLog($"Sync Caption Changed: {caption}");

            // When the caption changes to "Save to Local" (or whatever the exact string is in your Revit version)
            // it means the Save to Central part has finished.
            if (caption.Contains("Open an existing project") && _currentSynCaption.Contains("Save the active project back to the Central Model"))
            {
                _queueReleasedEarly = true;

                Utl.WriteLog("Detected 'Save to Local'. Releasing queue early via ProgressChanged event!");

                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        // 🔹 CHANGED: Pass the cached document
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
        // ── Win32 helpers for closing Revit's native sync progress dialog ──────
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private const uint WM_CLOSE = 0x0010;

        private static void TryCloseRevitSyncDialog()
        {
            try
            {
                System.Threading.Thread.Sleep(500);
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var sb = new System.Text.StringBuilder(256);

                EnumWindows((hwnd, _) =>
                {
                    uint windowPid;
                    GetWindowThreadProcessId(hwnd, out windowPid);
                    if (windowPid != (uint)pid) return true;

                    sb.Clear();
                    GetWindowText(hwnd, sb, sb.Capacity);
                    string title = sb.ToString();

                    if (title.IndexOf("Sync With Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        title.IndexOf("Synchronize with Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        title.IndexOf("Synchronising with Central", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        Utl.WriteLog($"Auto-closed Revit sync dialog: '{title}'");
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Could not auto-close Revit sync dialog: {ex.Message}");
            }
        }
        // ────────────────────────────────────────────────────────────────────────

        private void OnRevitIdlingDelay(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            // Let Idling fire repeatedly until 3 real seconds have passed to let the UI settle
            if (_startupStopwatch.ElapsedMilliseconds < 3000) return;

            // Time is up. Unsubscribe immediately so this never runs again!
            var uiApp = sender as UIApplication;
            if (uiApp != null)
            {
                uiApp.Idling -= OnRevitIdlingDelay;
            }
            _startupStopwatch.Stop();

            Utl.WriteLog($"[Plugin] Idling delay finished. Actual elapsed: {_startupStopwatch.ElapsedMilliseconds}ms. Evaluating Batch Task...");

            try
            {
                string json = File.ReadAllText(_batchFilePath);
                var instruction = JsonConvert.DeserializeObject<models.BatchInstruction>(json);

                if (instruction != null && instruction.IsValid())
                {
                    Utl.WriteLog("[Plugin] Batch task is valid. Launching Batch Processor...");

                    // 🔹 We are now safely inside a valid Revit API context! 🔹
                    var processor = new queue.BatchProcessor(uiApp, instruction, _batchFilePath);
                    processor.Start();
                }
                else
                {
                    Utl.WriteLog("[Plugin] Batch task is expired or invalid. Deleting file and ignoring.");
                    File.Delete(_batchFilePath);
                }
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"[Plugin] Critical failure in boot trigger: {ex.Message}");
                if (File.Exists(_batchFilePath)) File.Delete(_batchFilePath);
            }
        }
    }
}