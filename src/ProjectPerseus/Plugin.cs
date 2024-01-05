using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ProjectPerseus.revit;
using ProjectPerseus.revit.plugin;


namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class Plugin : IExternalApplication
    {
        private Config config = Config.Instance; 
        
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
            doOnSync(e);
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
                
                if(Config.Instance.FullSyncNextSync)
                {
                    Log.Info("Full sync requested - uploading all elements.");
                    Config.Instance.FullSyncNextSync = false;
                
                    var elements = new RevitFacade(e.Document).GetAllElements();
                    new ProjectPerseusWeb(config.BaseUrl, config.ApiToken).UploadElements(elements);
                }
                else
                {
                    Log.Info("Incremental sync requested - uploading changed elements.");
                    // todo: incremental sync
                }
                
                // dump json
                // Utl.JsonDump(elements, "ElementList");
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
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