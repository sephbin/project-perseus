using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ProjectPerseus.revit;
using Sentry;


namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class Plugin : IExternalApplication
    {
        private readonly Config _config = Config.Instance; 
        
        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;

            return Result.Succeeded;
        }

        private void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
        }

        private void OnDocumentSynchronizedWithCentral(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            using(var sentry = new Utl.Sentry())
            {
                try
                {
                    doOnSync(e);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                }
            }
        }

        private void doOnSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                if(uploadConfigIsValid() == false)
                {
                    Log.Warn("Upload config is not valid - skipping upload.");
                    return;
                }

                var revit = new RevitFacade(e.Document);
                
                if(Config.Instance.FullSyncNextSync)
                {
                    Log.Info("Full sync requested - uploading all elements.");
                    Config.Instance.FullSyncNextSync = false;
                
                    PerformFullSync(revit);
                }
                else
                {
                    Log.Info("Incremental sync requested - uploading changed elements.");
                    PerformIncrementalSync(revit);
                }
                
                // dump json
                // Utl.JsonDump(elements, "ElementList");
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }

        private void PerformFullSync(RevitFacade revit)
        {
            var elements = revit.GetAllElements();
            new ProjectPerseusWeb(_config.BaseUrl, _config.ApiToken).UploadElements(elements);
        }

        private void PerformIncrementalSync(RevitFacade revit)
        {
            var elementChangeSet = revit.GetElementChangeSet(_config.LastSyncVersionGuid);
            // todo:
        }

        private bool uploadConfigIsValid()
        {
            return !string.IsNullOrEmpty(_config.ApiToken) 
                   && _config.BaseUrl != null 
                   && Utl.IsValidUrl(_config.BaseUrl);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            return Result.Succeeded;
        }
    }
}