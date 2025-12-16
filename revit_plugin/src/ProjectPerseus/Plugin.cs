using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using System.IO;
using static System.Net.Mime.MediaTypeNames;
using System.Reflection;
using System.Windows.Media.Imaging;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;
using System.Reflection.PortableExecutable;

namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class Plugin : IExternalApplication
    {
        private readonly Config _config = Config.Instance;

        //This adds the "OnDocumentSynchronizedWithCentral" function to the "DocumentSynchronizedWithCentral" event stack
        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizingWithCentral += OnDocumentSynchronizingWithCentral;
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            AddRibbonPanel(application);
            try
            {
                string roamingFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string appSpecificFolderPath = Path.Combine(roamingFolderPath, "ProjectPerseus");
                string filePath = Path.Combine(appSpecificFolderPath, "medusa.log");
                File.WriteAllText(filePath, String.Empty);
            }
            catch {
                Console.WriteLine($"Error clearing log file");
            }
            return Result.Succeeded;
        }


        private void WriteLog(string content)
        {
            string roamingFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appSpecificFolderPath = Path.Combine(roamingFolderPath, "ProjectPerseus");
            Directory.CreateDirectory(appSpecificFolderPath); // Creates the directory if it doesn't exist
            string filePath = Path.Combine(appSpecificFolderPath, "medusa.log");
            try
            {
                File.AppendAllText(filePath, content + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving file: {ex.Message}");
            }
        }

        //This is a wrapper for doOnPriorToSync, as it doesn't require as many arguments
        private void OnDocumentSynchronizingWithCentral(object sender, DocumentSynchronizingWithCentralEventArgs e)
        {
            doOnPriorToSync(e);

        }

        private void doOnPriorToSync(DocumentSynchronizingWithCentralEventArgs e)
        {
            try
            {
                WriteLog("Sync initiated – contacting web server...");

                if (UploadConfigIsValid() == false)
                {
                    WriteLog("Upload config invalid – skipping preliminary check.");
                    return;
                }

                var revit = new RevitFacade(e.Document);
                var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);
                var baseUrl = _config.BaseUrl;

                // --- 1. Check Sync Queue Status ---
                var queueEndpoint = $"{baseUrl}/../syncboat/getCurrentQueue/{docGuid}/";

                string queueResponseJson = Utl.WebHelper.Get(queueEndpoint, _config.ApiToken, null);

                // Deserialize the response into the new model structure
                var queueStatus = JsonConvert.DeserializeObject<SyncQueueResponse>(queueResponseJson);

                // Get the list of users and the count
                List<string> usersInQueue = queueStatus?.Queue ?? new List<string>();
                int queueCount = usersInQueue.Count;

                // --- 2. Prompt User if Queue Exists ---
                if (queueCount > 0)
                {
                    // Format the list of users for display in the TaskDialog.
                    // Using Environment.NewLine for clear, multi-line formatting.
                    string queueList = string.Join(Environment.NewLine, usersInQueue);

                    string message = $"**{queueCount} user(s) are currently in the sync queue:**{Environment.NewLine}{Environment.NewLine}{queueList}{Environment.NewLine}{Environment.NewLine}Do you want to **Sync Anyway** or **Cancel** and try later?";

                    TaskDialog mainDialog = new TaskDialog("Sync Queue Alert")
                    {
                        MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                        MainInstruction = "Sync Queue Alert",
                        CommonButtons = TaskDialogCommonButtons.None, // Hide default buttons
                        AllowCancellation = true,
                        TitleAutoPrefix = false
                    };

                    // Set the detailed message content
                    mainDialog.FooterText = message; // Use FooterText for detailed, multi-line content

                    // Add custom command links (buttons)
                    mainDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Sync Anyway");
                    mainDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancel Sync");

                    TaskDialogResult result = mainDialog.Show();

                    // --- 3. Cancel Revit Sync if requested ---
                    if (result == TaskDialogResult.CommandLink2 || result == TaskDialogResult.Cancel)
                    {
                        e.Cancel();
                        WriteLog("User cancelled sync due to queue alert.");
                        return;
                    }

                    WriteLog("User chose to Sync Anyway despite queue alert. Proceeding to preliminary check.");
                }
                else
                {
                    WriteLog("No one in the sync queue. Proceeding with preliminary check.");
                }
                // --- End Queue Check ---


                var app = e.Document.Application;
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

                // Preliminary call to the web API to verify or register sync start
                var preSyncEndpoint = $"{baseUrl}/presync/{docGuid}";
                string response = Utl.WebHelper.Post(preSyncEndpoint, _config.ApiToken, jsonPayload);

                WriteLog($"Preliminary sync request sent. Response: {response}");
            }
            catch (Exception ex)
            {
                WriteLog($"Error during preliminary sync: {ex.Message}");
            }
        }
        private void doOnPostSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                WriteLog("Sync finished – contacting web server...");

                if (UploadConfigIsValid() == false)
                {
                    WriteLog("Upload config invalid – skipping preliminary check.");
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
                var preSyncEndpoint = $"{baseUrl}/postsync/{docGuid}";
                string response = Utl.WebHelper.Post(preSyncEndpoint, _config.ApiToken, jsonPayload);

                WriteLog($"Post sync request sent. Response: {response}");
            }
            catch (Exception ex)
            {
                WriteLog($"Error during post sync: {ex.Message}");
            }
        }
        //This appears to be a wrapper for the doOnSync function so it doesn't need as many arguments
        private void OnDocumentSynchronizedWithCentral(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            //WriteLog("OnDocumentSynchronizedWithCentral");
            doOnSync(e);
            doOnPostSync(e);
            //WriteLog("// OnDocumentSynchronizedWithCentral");
        }

        //Decides what type of sync to do
        private void doOnSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            //WriteLog("doOnSync");
            using (var sentry = new Utl.SentryContext())
            {
                try
                {
                    if (UploadConfigIsValid() == false)
                    {
                        Log.Warn("Upload config is not valid - skipping upload.");
                        //WriteLog("Upload config is not valid - skipping upload.");
                        return;
                    }

                    // record elapsed time
                    var watch = System.Diagnostics.Stopwatch.StartNew();

                    try
                    {
                        var revit = new RevitFacade(e.Document);

                        WriteLog("Before PerformIncrementalSync");
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
                        WriteLog($"Error performing sync: {ex.Message}");

                    }

                    watch.Stop();
                    //Log.Info($"Sync completed in {watch.Elapsed:hh\\:mm\\:ss}");
                    WriteLog($"Sync completed in {watch.Elapsed:hh\\:mm\\:ss}");
                    // dump json
                    // Utl.JsonDump(elements, "ElementList");
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                    WriteLog(ex.ToString());
                }
            }
            //WriteLog("  //doOnSync");
        }

        private void PerformFullSync(RevitFacade revit)
        {

            //Create a Perseus Source and Project Set
            try
            {
                // Create a Perseus Source and Project Set
                var doc = revit.Document;
                var app = doc.Application;
                var thisdocGuid = ModelGuidStorage.GetOrCreate(doc);
                var baseUrl = _config.BaseUrl;

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
                    WriteLog($"Failed to read project info: {ex.Message}");
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
                string response = Utl.WebHelper.Post(metadataEndpoint, _config.ApiToken, jsonMetadata);
                WriteLog($"Metadata upload response: {response}");
            }
            catch (Exception ex) { WriteLog(ex.ToString()); }


            var elements = revit.GetAllElements();    
            WriteLog($"PerformFullSync: Found {elements.Count} elements");
            var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);
            WriteLog(docGuid);
            var elementDeltaList = ElementDelta.CreateList(ElementDelta.DeltaAction.Create, elements, revit.Document, docGuid);
            //SubmitElementDeltas(elementDeltaList);
            WriteLog("PerformFullSync: Created elementDeltaList");
            var filteredElementDeltaList = elementDeltaList;
            try { filteredElementDeltaList = elementDeltaList.FilterByCategoryName(new[] { "Rooms", "Doors" }); }
            catch (Exception ex) { WriteLog(ex.ToString()); }
            WriteLog("PerformFullSync: Filtered Element Delta List");
            SubmitElementState(filteredElementDeltaList);
        }

        private void PerformIncrementalSync(RevitFacade revit)
        {
            //WriteLog("PerformIncrementalSync - Before GetElementChangeSet");
            //(_config.LastSyncVersionGuid.ToString());
            
            var _baseUrl = _config.BaseUrl;
            var docId = ModelGuidStorage.GetOrCreate(revit.Document);
            WriteLog(docId);
            var StateEndpoint = $"{_baseUrl}/getstate/{docId}";

            string stateJson = Utl.WebHelper.Get(StateEndpoint,null,null);
            
            // 🔹 Step 2: Parse JSON { "value": "f178df1e-b572-401c-af59-af6f34336834" }
            var json = JsonConvert.DeserializeObject<Dictionary<string, string>>(stateJson);


            // lastSyncVersionGuid = _config.LastSyncVersionGuid;
            var lastSyncVersionGuid = Guid.Parse(json["value"]);
            
            WriteLog(lastSyncVersionGuid.ToString());

            //var elementChangeSet = revit.GetElementChangeSet(lastSyncVersionGuid)
            var elementChangeSet = revit.GetElementChangeSet(lastSyncVersionGuid) ;
            
            //WriteLog("PerformIncrementalSync - Before Change Set If Satement");
            if (elementChangeSet.ContainsChanges())
            {
                var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);
                WriteLog(docGuid);
                var elementDeltaList = ElementDelta.CreateListFromChangeSet(elementChangeSet, revit.Document, docGuid);
                elementDeltaList = elementDeltaList.FilterByCategoryName(new[] { "Rooms", "Doors" });
                
                //var elementDeltaDeletedList = ElementDelta.CreateDeletedListFromChangeSet(elementChangeSet);
                var elementDeltaDeletedList = new List<int>();
                //elementDeltaList.AddRange(elementDeltaDeletedList);
                WriteLog("About to run SubmitElementDeltas");

                SubmitElementDeltas(elementDeltaList, elementDeltaDeletedList, revit.Document);
            }
            else 
            {
                Log.Info("No changes detected - skipping upload.");
                WriteLog("No changes detected - skipping upload.");
            }
        }

        private void SubmitElementDeltas(IList<ElementDelta> elements, IList<int> deleted, Document doc)
        {
            new ProjectPerseusWeb(_config.BaseUrl, _config.ApiToken).SubmitElementDeltas(elements, deleted, doc);
        }
        private void SubmitElementState(IList<ElementDelta> elements)
        {
            new ProjectPerseusWeb(_config.BaseUrl, _config.ApiToken).SubmitElementState(elements);
        }
        private bool UploadConfigIsValid()
        {
            return !string.IsNullOrEmpty(_config.ApiToken) 
                   && _config.BaseUrl != null 
                   && Utl.IsValidUrl(_config.BaseUrl);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
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
            BitmapImage pb1Image = new BitmapImage(new Uri("pack://application:,,,/ProjectPerseus;component/resources/perseus.png"));
            pb1.LargeImage = pb1Image;

            
            PushButtonData settingsBtnData = new PushButtonData(
            "Button_Settings",
            "Settings",
            thisAssemblyPath,
            "ProjectPerseus.Commands.OpenSettingsCommand");

            PushButton settingsBtn = ribbonPanel.AddItem(settingsBtnData) as PushButton;
            settingsBtn.ToolTip = "Change Perseus Settings like: API Token and Upload URL";
            BitmapImage settingsBtnImage = new BitmapImage(new Uri("pack://application:,,,/ProjectPerseus;component/resources/settings.png"));
            settingsBtn.LargeImage = settingsBtnImage;

        }
    }
}