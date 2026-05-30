using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.auth;
using ProjectPerseus.config;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.web;

using ProjectPerseus.logging;
using ProjectPerseus.util;
namespace ProjectPerseus.sync
{
    // Full-state sync: iterates every element in the model and pushes the entire payload
    // to either the Django backend (PerformFullSync) or a JSON file (PerformFullSyncToFile).
    // Used as the baseline when incremental history is missing or the user manually triggers
    // a full re-upload from the ribbon.
    public static class FullSyncRunner
    {
        public static void PerformFullSync(RevitFacade revit)
        {
            try
            {
                Log.Info("Start Watch");
                var watch = Stopwatch.StartNew();

                var doc = revit.Document;
                var app = doc.Application;
                var thisdocGuid = ModelGuidStorage.GetOrCreate(doc);
                var baseUrl = Config.Instance.BaseUrl;

                string fileName = doc.Title;
                string filePath = doc.PathName;
                string revitVersion = app.VersionNumber;

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
                    Log.Info($"Failed to read project info: {ex.Message}");
                }

                var metadata = new
                {
                    documentGuid = thisdocGuid,
                    fileName = fileName,
                    filePath = filePath,
                    revitVersion = revitVersion,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    projectInfo = new
                    {
                        number = projectNumber,
                        name = projectName,
                        client = clientName
                    }
                };

                var metadataEndpoint = $"{baseUrl}/registersource/";
                string jsonMetadata = JsonConvert.SerializeObject(metadata);
                string response = WebHelper.Post(metadataEndpoint, AuthService.GetAuthTokenSafely(), jsonMetadata);
                JObject json = JObject.Parse(response);
                Log.Info($"Metadata upload response: {response}");

                var elements = revit.GetAllElements();
                Log.Info($"PerformFullSync: Found {elements.Count} elements");

                var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);
                Log.Info(docGuid);

                var elementDeltaList = ElementDelta.CreateList(ElementDelta.DeltaAction.Create, elements, revit.Document, docGuid).ToList();
                Log.Info("PerformFullSync: Created elementDeltaList");

                var filteredElementDeltaList = new List<ElementDelta>();
                var categories = json["source"]["parameter_dict"]["perseusCategories"].ToObject<List<string>>();

                try { filteredElementDeltaList = elementDeltaList.FilterByCategoryName(categories).ToList(); }
                catch (Exception ex) { Log.Info(ex.ToString()); }

                try
                {
                    Log.Info("Harvesting Categories...");
                    var categoryDeltas = new List<ElementDelta>();

                    foreach (Category cat in CategoryHarvester.GetAllCategories(revit.Document))
                    {
                        var catAdapter = new ProjectPerseus.revit.adapters.ArdbCategoryAdapter(cat);
                        var delta = new ElementDelta(ElementDelta.DeltaAction.Update, catAdapter, revit.Document, docGuid);
                        categoryDeltas.Add(delta);
                    }

                    Log.Info($"Added {categoryDeltas.Count} Categories to the payload.");
                    filteredElementDeltaList.AddRange(categoryDeltas);
                }
                catch (Exception ex)
                {
                    Log.Info($"Error harvesting categories: {ex.Message}");
                }

                try
                {
                    bool collectConnected = false;
                    if (json["source"]?["parameter_dict"]?["perseusOption_collectConnectedElements"] != null)
                    {
                        collectConnected = (bool)json["source"]["parameter_dict"]["perseusOption_collectConnectedElements"];
                    }

                    if (collectConnected)
                    {
                        Log.Info("Option 'collectConnectedElements' is TRUE. Harvesting references...");

                        HashSet<long> referencedIds = ElementDelta.GetReferencedIds(filteredElementDeltaList);
                        var existingIds = filteredElementDeltaList.Select(x => x.Element.Id).ToHashSet();
                        referencedIds.ExceptWith(existingIds);

                        Log.Info($"Found {referencedIds.Count} additional connected elements.");

                        if (referencedIds.Count > 0)
                        {
                            var connectedDeltas = ElementDelta.CreateListFromIds(referencedIds, revit.Document, docGuid);
                            filteredElementDeltaList.AddRange(connectedDeltas);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"Error in CollectConnectedElements logic: {ex.Message}");
                }

                Log.Info("PerformFullSync: Filtered Element Delta List");
                StateSubmitter.SubmitElementState(filteredElementDeltaList);

                watch.Stop();
                Log.Info("End Watch");
                Log.Info($"Full Upload completed in {watch.Elapsed:hh\\:mm\\:ss}");
            }
            catch (Exception ex) { Log.Info(ex.ToString()); }
        }

        public static void PerformFullSyncToFile(RevitFacade revit, string outputDir, IList<string> categories, bool collectConnectedElements)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                var doc = revit.Document;
                var docGuid = ModelGuidStorage.GetOrCreate(doc);

                var elements = revit.GetAllElements();
                Log.Info($"PerformFullSyncToFile: Found {elements.Count} elements");

                var elementDeltaList = ElementDelta.CreateList(ElementDelta.DeltaAction.Create, elements, doc, docGuid).ToList();

                if (categories != null && categories.Count > 0)
                {
                    elementDeltaList = elementDeltaList.FilterByCategoryName(categories).ToList();
                    Log.Info($"PerformFullSyncToFile: Filtered to {elementDeltaList.Count} elements by category");
                }

                try
                {
                    foreach (Category cat in CategoryHarvester.GetAllCategories(doc))
                    {
                        var catAdapter = new ProjectPerseus.revit.adapters.ArdbCategoryAdapter(cat);
                        elementDeltaList.Add(new ElementDelta(ElementDelta.DeltaAction.Update, catAdapter, doc, docGuid));
                    }
                    Log.Info($"PerformFullSyncToFile: Added categories, total {elementDeltaList.Count} items");
                }
                catch (Exception ex)
                {
                    Log.Info($"PerformFullSyncToFile: Error harvesting categories: {ex.Message}");
                }

                if (collectConnectedElements)
                {
                    try
                    {
                        HashSet<long> referencedIds = ElementDelta.GetReferencedIds(elementDeltaList);
                        var existingIds = elementDeltaList.Select(x => x.Element.Id).ToHashSet();
                        referencedIds.ExceptWith(existingIds);
                        Log.Info($"PerformFullSyncToFile: Found {referencedIds.Count} additional connected elements.");
                        if (referencedIds.Count > 0)
                            elementDeltaList.AddRange(ElementDelta.CreateListFromIds(referencedIds, doc, docGuid));
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"PerformFullSyncToFile: Error collecting connected elements: {ex.Message}");
                    }
                }

                Directory.CreateDirectory(outputDir);
                string safeName = string.Concat(doc.Title.Split(Path.GetInvalidFileNameChars()));
                string outputPath = Path.Combine(outputDir, $"{safeName}.json");
                File.WriteAllText(outputPath, JsonUtils.SerializeToJson(elementDeltaList, null));

                watch.Stop();
                Log.Info($"PerformFullSyncToFile: Wrote {elementDeltaList.Count} elements to {outputPath} in {watch.Elapsed:hh\\:mm\\:ss}");
            }
            catch (Exception ex)
            {
                Log.Info($"PerformFullSyncToFile failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
