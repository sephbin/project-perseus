using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ARDB = Autodesk.Revit.DB;


namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProjectPerseusApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;

            return Result.Succeeded;
        }

        private void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
        }

        private void OnDocumentSynchronizedWithCentral(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            doOnSync(e);
        }

        private static void doOnSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                if(uploadConfigIsValid() == false)
                {
                    Log.Warn("Upload config is not valid - skipping upload.");
                    return;
                }
                
                var elements = new ElementExtractor(e.Document).ExtractElements();
                new ProjectPerseusWeb(Config.BaseUrl, Config.ApiToken).UploadElements(elements);
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }

        private static bool uploadConfigIsValid()
        {
            return !string.IsNullOrEmpty(Config.ApiToken) 
                   && Config.BaseUrl != null 
                   && Utl.IsValidUrl(Config.BaseUrl);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            return Result.Succeeded;
        }
    }
}