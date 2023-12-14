using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ARDB = Autodesk.Revit.DB;


namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProjectPerseusApp : IExternalApplication
    {
        private static readonly HttpClient client = new HttpClient();

        private Config config = Config.Load($"{Directory.GetCurrentDirectory()}/config.json");

        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            return Result.Succeeded;
        }

        private void OnDocumentSynchronizedWithCentral(object sender,
            Autodesk.Revit.DB.Events.DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                var doc = e.Document;
                var elements = new List<models.Element>();

                // Create a filtered element collector to get all elements in the document
                FilteredElementCollector collector = new FilteredElementCollector(doc);

                // Use the WhereElementIsNotElementType filter to exclude element types
                var allElements = collector.WhereElementIsNotElementType().ToElements();

                foreach (var element in allElements)
                {
                    elements.Add(models.Element.FromARDBElement(element));
                }

                SubmitElementListToApi(elements);

                // ToJsonFile(eles);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ERROR", ex.ToString());
            }
        }

        private void SubmitElementListToApi(List<models.Element> eles)
        {
            var jsonString = Utl.SerializeToString(eles, null);
            Post(config.ElementsEndpoint, jsonString);
        }

        private string Post(string endpoint, string json)
        {
            return DeliverToApiEndpoint(endpoint, json, "POST");
        }

        private string DeliverToApiEndpoint(string endpoint, string json, string method)
        {
            var httpWebRequest = (HttpWebRequest)WebRequest.Create(endpoint);
            httpWebRequest.ContentType = "application/json";
            httpWebRequest.Method = method;
            httpWebRequest.Headers["Authorization"] = $"Token {config.ApiToken}";

            using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
            {
                streamWriter.Write(json);
            }

            var httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();
            using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
            {
                return streamReader.ReadToEnd();
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
            return Result.Succeeded;
        }

        private static void ToJsonFile(object o)
        {
            var workingDirectory = Directory.GetCurrentDirectory();
            Utl.PrettyWriteJson(o,
                $"{workingDirectory}/elements.json",
                null);
        }
    }
}