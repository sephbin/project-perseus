using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                var eles = new List<Element>();

                // Create a filtered element collector to get all elements in the document
                FilteredElementCollector collector = new FilteredElementCollector(doc);

                // Use the WhereElementIsNotElementType filter to exclude element types
                var allElements = collector.WhereElementIsNotElementType().ToElements();

                foreach (var element in allElements)
                {
                    eles.Add(Element.FromARDBElement(element));
                }

                SubmitElementListToApi(eles);

                // ToJsonFile(eles);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ERROR", ex.ToString());
            }
        }

        private void SubmitElementListToApi(List<Element> eles)
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

        private class Element
        {
            public int id { get; } // todo: this can change apparently - use UniqueId  instead

            public string name { get; }
            public string comments { get; }

            private Element(int id,
                string name,
                string comments)
            {
                this.id = id;
                this.name = name;
                this.comments = comments;
            }

            private const string CommentsParameterKey = "Comments";

            public static Element FromARDBElement(ARDB.Element element)
            {
                if (element is null) throw new ArgumentNullException(nameof(element));
                if (element.Id is null) throw new ArgumentNullException(nameof(element.Id));
                if (element.Name is null) throw new ArgumentNullException(nameof(element.Name));
                String comments = null;
                try
                {
                    // if anything goes wrong here, swallow it and just don't set the comments
                    // if (element.ParametersMap is null) throw new ArgumentNullException(nameof(element.ParametersMap));
                    if (element.ParametersMap.Contains(CommentsParameterKey))
                        comments = element.ParametersMap.get_Item(CommentsParameterKey).AsString();
                }
                catch (Exception ex)
                {
                }

                return new Element(
                    element.Id.IntegerValue, // todo: use unique ID
                    element.Name,
                    comments);
            }
        }
    }
}