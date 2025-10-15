using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using System.IO;
using static System.Net.Mime.MediaTypeNames;
using System.Reflection;


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
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            AddRibbonPanel(application);
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
                File.AppendAllText(filePath, content+Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving file: {ex.Message}");
            }   
        }
        
        //This appears to be a wrapper for the doOnSync function so it doesn't need as many arguments
        private void OnDocumentSynchronizedWithCentral(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            WriteLog("OnDocumentSynchronizedWithCentral");
            doOnSync(e);
            WriteLog("// OnDocumentSynchronizedWithCentral");
        }

        //Decides what type of sync to do
        private void doOnSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            WriteLog("  doOnSync");
            using (var sentry = new Utl.SentryContext())
            {
                try
                {
                    if (UploadConfigIsValid() == false)
                    {
                        Log.Warn("Upload config is not valid - skipping upload.");
                        WriteLog("Upload config is not valid - skipping upload.");
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
                        WriteLog("Before _config.LastSyncVersionGuid");
                        _config.LastSyncVersionGuid = RevitFacade.GetDocumentVersionGuid(revit.Document);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(new Exception($"Error performing sync: {ex.Message}", ex));
                        WriteLog($"Error performing sync: {ex.Message}");

                    }

                    watch.Stop();
                    Log.Info($"Sync completed in {watch.Elapsed:hh\\:mm\\:ss}");
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
            WriteLog("  //doOnSync");
        }

        private void PerformFullSync(RevitFacade revit)
        {
                var elements = revit.GetAllElements();
                var elementDeltaList = ElementDelta.CreateList(ElementDelta.DeltaAction.Create, elements, revit.Document);
                //SubmitElementDeltas(elementDeltaList);
                SubmitElementState(elementDeltaList);
        }

        private void PerformIncrementalSync(RevitFacade revit)
        {
            WriteLog("PerformIncrementalSync - Before GetElementChangeSet");
            //(_config.LastSyncVersionGuid.ToString());
            
            var _baseUrl = _config.BaseUrl;
            var docId = revit.Document.ProjectInformation.UniqueId;
            var StateEndpoint = $"{_baseUrl}/getstate/{docId}";

            string stateJson = Utl.WebHelper.Get(StateEndpoint,null,null);
            
            // 🔹 Step 2: Parse JSON { "value": "f178df1e-b572-401c-af59-af6f34336834" }
            var json = JsonConvert.DeserializeObject<Dictionary<string, string>>(stateJson);


            // lastSyncVersionGuid = _config.LastSyncVersionGuid;
            var lastSyncVersionGuid = Guid.Parse(json["value"]);
            
            WriteLog(lastSyncVersionGuid.ToString());

            var elementChangeSet = revit.GetElementChangeSet(lastSyncVersionGuid);
            
            WriteLog("PerformIncrementalSync - Before Change Set If Satement");
            if (elementChangeSet.ContainsChanges())
            {
                var elementDeltaList = ElementDelta.CreateListFromChangeSet(elementChangeSet, revit.Document);
                SubmitElementDeltas(elementDeltaList);
            }
            else 
            {
                Log.Info("No changes detected - skipping upload.");
                WriteLog("No changes detected - skipping upload.");
            }
        }

        private void SubmitElementDeltas(IList<ElementDelta> elements)
        {
            new ProjectPerseusWeb(_config.BaseUrl, _config.ApiToken).SubmitElementDeltas(elements);
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
                "Full" + System.Environment.NewLine + "  Upload  ",
                thisAssemblyPath,
                "ProjectPerseus.Commands.PerformFullUploadCommand");

            PushButton pb1 = ribbonPanel.AddItem(b1Data) as PushButton;
            pb1.ToolTip = "Upload all elements to external database";
            //BitmapImage pb1Image = new BitmapImage(new Uri("pack://application:,,,/PerseusRibbon;component/Resources/totalLength.png"));
            //pb1.LargeImage = pb1Image;
        }
    }
}