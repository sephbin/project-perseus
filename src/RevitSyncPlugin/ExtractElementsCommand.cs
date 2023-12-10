using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Xml.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using ARDB = Autodesk.Revit.DB;


namespace RevitSyncPlugin
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OnSyncTest : IExternalApplication
    {
        private static readonly HttpClient client = new HttpClient();
        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.DocumentChanged += ControlledApplication_DocumentChanged;
            return Result.Succeeded;
        }

        private void ControlledApplication_DocumentChanged(object sender, ARDB.Events.DocumentChangedEventArgs e)
        {

            try
            {
                var modifiedElementIds = e.GetModifiedElementIds();
                foreach (var elementId in modifiedElementIds)
                {
                    var ele = e.GetDocument().GetElement(elementId);
                    var id = ele.Id;
                    var name = ele.Name;
                    var comment = "";
                    try
                    {
                        comment = ele.ParametersMap.get_Item("Comments").AsString();
                    }
                    catch (Exception ex) { }

                    var element = new Element(id.IntegerValue, name, comment);
                    // todo: only post when something we care about changed
                    SubmitElementUpdateToApi(element);
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ERROR", e.ToString());
            }

            // todo: process added and deleted elements
}

        private void OnDocumentSynchronizedWithCentral(object sender, Autodesk.Revit.DB.Events.DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                ARDB.Document doc = e.Document;

                var categoryDataExtractor = new CategoryDataExtractor(doc);
                var categories = categoryDataExtractor.Extract();

                var elementDataExtractor = new ElementDataExtractor(doc);
                // var categorisedElements = new List<Category>();
                var eles = new List<Element>();
                foreach (var category in categories)
                {
                    eles.AddRange(elementDataExtractor.Extract(category).Select(
                        ele =>
                        {
                            var id = ele.Id;
                            var name = ele.Name;
                            var comment = "";
                            try
                            {
                                comment = ele.ParametersMap.get_Item("Comments").AsString();
                            }
                            catch (Exception ex)
                            {
                            }

                            return new Element(id.IntegerValue, name, comment);
                        }
                    ).ToList());
                    // categorisedElements.Add(new Category(category, eles));
                }

                SubmitElementListToApi(eles);

                // ToJsonFile(categorisedElements);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ERROR", e.ToString());
            }

        }

        private void SubmitElementListToApi(List<Element> eles)
        {
            var jsonString = Utl.SerializeToString(eles, null);
            var result = Post("http://127.0.0.1:8000/elements/", jsonString);
        }

        private void SubmitElementUpdateToApi(Element element)
        {
            var jsonString = Utl.SerializeToString(element, null);
            var result = Put($"http://127.0.0.1:8000/elements/{element.id}/", jsonString);
        }

        private string Put(string endpoint, string json)
        {
            return DeliverToApiEndpoint(endpoint, json, "PUT");
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
            Utl.PrettyWriteJson(o, "C:\\Users\\lladdy\\Desktop\\A WEBSERVER\\elements.json", null);
        }

        private class Category
        {
            public int Id { get; } // todo: this can change apparently

            public string Name { get; }

            public ARDB.CategoryType CategoryType { get; }

            public bool IsTagCategory { get; }

            public bool IsSubcategory { get; }

            public bool CanAddSubcategory { get; }

            public bool AllowsBoundParameters { get; }

            public bool HasMaterialQuantities { get; }

            public bool IsCuttable { get; }
            
            public List<Element> Elements { get; }

            private Category(int id,
                string name,
                ARDB.CategoryType categoryType,
                bool isTagCategory,
                bool isSubcategory,
                bool canAddSubcategory,
                bool allowsBoundParameters,
                bool hasMaterialQuantities,
                bool isCuttable,
                List<Element> elements)
            {
                Id = id;
                Name = name;
                CategoryType = categoryType;
                IsTagCategory = isTagCategory;
                IsSubcategory = isSubcategory;
                CanAddSubcategory = canAddSubcategory;
                AllowsBoundParameters = allowsBoundParameters;
                HasMaterialQuantities = hasMaterialQuantities;
                IsCuttable = isCuttable;
                Elements = elements;
            }

            public Category(ARDB.Category category, List<Element> elements) : this(
                category.Id.IntegerValue,
                category.Name,
                category.CategoryType,
                category.IsTagCategory,
                category.Parent is object,
                category.CanAddSubcategory,
                category.AllowsBoundParameters,
                category.HasMaterialQuantities,
                category.IsCuttable,
                elements)
            {
            }
        }


        private class Element
        {
            public int id { get; } // todo: this can change apparently - use UniqueId  instead

            public string name { get; }
            public string comments { get; }

            public Element(int id,
                string name,
                string comments)
            {
                this.id = id;
                this.name = name;
                this.comments = comments;
            }

            // public Element(ARDB.Element element) : this(
            //     element.Id.IntegerValue,
            //     element.Name,
            //     element.ParametersMap.get_Item("Comments").AsString())
            // {
            // }
        }
    }
}