using System;
using System.IO;
using System.Net.Http;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using ARDB = Autodesk.Revit.DB;


namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProjectPerseusApp : IExternalApplication
    {
        private static readonly HttpClient client = new HttpClient();

        private Config _config = Config.Load($"{Directory.GetCurrentDirectory()}/config.json");

        private ProjectPerseusWeb web;

        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;

            web = new ProjectPerseusWeb(_config.BaseUrl, _config.ApiToken);

            return Result.Succeeded;
        }

        private void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
        }

        private void OnDocumentSynchronizedWithCentral(object sender,
            Autodesk.Revit.DB.Events.DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                var elements = new ElementExtractor(e.Document).ExtractElements();
                web.UploadElements(elements);
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            return Result.Succeeded;
        }
    }
}