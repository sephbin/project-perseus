using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.config;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.web;

namespace ProjectPerseus.sync
{
    // Incremental sync: uses Revit's GetElementChangeSet against the last-known versionGuid
    // (stored server-side for PerformIncrementalSync, or read from the JSON file's first
    // source_state for PerformIncrementalSyncToFile) to push only created/modified/deleted
    // elements. Falls back to FullSyncRunner if local PacCache history is missing.
    public static class IncrementalSyncRunner
    {
        public static void PerformIncrementalSync(RevitFacade revit)
        {
            try
            {
                var _baseUrl = Config.Instance.BaseUrl;
                var docId = ModelGuidStorage.GetOrCreate(revit.Document);
                Utl.WriteLog(docId);
                var StateEndpoint = $"{_baseUrl}/getstate/{docId}";

                string stateJson = WebHelper.Get(StateEndpoint, null, null);
                JObject json = JObject.Parse(stateJson);

                var lastSyncVersionGuid = Guid.Parse(json["value"].ToString());
                Utl.WriteLog(lastSyncVersionGuid.ToString());

                var elementChangeSet = revit.GetElementChangeSet(lastSyncVersionGuid);

                if (elementChangeSet.ContainsChanges())
                {
                    var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);

                    var elementDeltaList = ElementDelta.CreateListFromChangeSet(elementChangeSet, revit.Document, docGuid);
                    var categories = json["source"]["parameter_dict"]["perseusCategories"].ToObject<List<string>>();
                    elementDeltaList = elementDeltaList.FilterByCategoryName(categories);

                    try
                    {
                        bool collectConnected = false;
                        if (json["source"]?["parameter_dict"]?["perseusOption_collectConnectedElements"] != null)
                        {
                            collectConnected = (bool)json["source"]["parameter_dict"]["perseusOption_collectConnectedElements"];
                        }

                        if (collectConnected)
                        {
                            Utl.WriteLog("Option 'collectConnectedElements' is TRUE. Harvesting references...");

                            HashSet<long> referencedIds = ElementDelta.GetReferencedIds(elementDeltaList);
                            var existingIds = elementDeltaList.Select(x => x.Element.Id).ToHashSet();
                            referencedIds.ExceptWith(existingIds);

                            Utl.WriteLog($"Found {referencedIds.Count} additional connected elements.");

                            if (referencedIds.Count > 0)
                            {
                                var connectedDeltas = ElementDelta.CreateListFromIds(referencedIds, revit.Document, docGuid);
                                elementDeltaList.AddRange(connectedDeltas);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Utl.WriteLog($"Error in CollectConnectedElements logic: {ex.Message}");
                    }

                    var elementDeltaDeletedList = ElementDelta.CreateDeletedListFromChangeSet(elementChangeSet);

                    Utl.WriteLog("About to run SubmitElementDeltas");
                    StateSubmitter.SubmitElementDeltas(elementDeltaList, elementDeltaDeletedList, revit.Document);
                }
                else
                {
                    Log.Info("No changes detected - skipping upload.");
                    Utl.WriteLog("No changes detected - skipping upload.");
                }
            }
            // PacCache is gone — Revit can't replay the change set. Fall back to a full sync
            // to re-establish a baseline.
            catch (Autodesk.Revit.Exceptions.ArgumentException ex) when (ex.Message.Contains("baseVersionGUID"))
            {
                Utl.WriteLog("WARNING: Local incremental history is missing or broken (PacCache likely cleared).");
                Utl.WriteLog("Automatically falling back to PerformFullSync...");
                FullSyncRunner.PerformFullSync(revit);
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"PerformIncrementalSync critically failed: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        public static void PerformIncrementalSyncToFile(RevitFacade revit, string outputDir, IList<string> categories, bool collectConnectedElements)
        {
            var doc = revit.Document;
            var docGuid = ModelGuidStorage.GetOrCreate(doc);
            string safeName = string.Concat(doc.Title.Split(Path.GetInvalidFileNameChars()));
            string outputPath = Path.Combine(outputDir, $"{safeName}.json");

            Guid? lastVersionGuid = ReadFirstSourceState(outputPath);
            if (lastVersionGuid == null)
            {
                Utl.WriteLog($"PerformIncrementalSyncToFile: No valid previous file at {outputPath}. Falling back to full sync.");
                FullSyncRunner.PerformFullSyncToFile(revit, outputDir, categories, collectConnectedElements);
                return;
            }

            Utl.WriteLog($"PerformIncrementalSyncToFile: Last source_state = {lastVersionGuid}");

            try
            {
                var watch = Stopwatch.StartNew();

                var elementChangeSet = revit.GetElementChangeSet(lastVersionGuid.Value);

                if (!elementChangeSet.ContainsChanges())
                {
                    Utl.WriteLog("PerformIncrementalSyncToFile: No changes detected.");
                    return;
                }

                var elementDeltaList = ElementDelta.CreateListFromChangeSet(elementChangeSet, doc, docGuid);
                if (categories != null && categories.Count > 0)
                    elementDeltaList = elementDeltaList.FilterByCategoryName(categories);

                if (collectConnectedElements)
                {
                    try
                    {
                        HashSet<long> referencedIds = ElementDelta.GetReferencedIds(elementDeltaList);
                        var existingIds = elementDeltaList.Select(x => x.Element.Id).ToHashSet();
                        referencedIds.ExceptWith(existingIds);
                        if (referencedIds.Count > 0)
                            elementDeltaList.AddRange(ElementDelta.CreateListFromIds(referencedIds, doc, docGuid));
                        Utl.WriteLog($"PerformIncrementalSyncToFile: {referencedIds.Count} connected elements added.");
                    }
                    catch (Exception ex)
                    {
                        Utl.WriteLog($"PerformIncrementalSyncToFile: Error collecting connected elements: {ex.Message}");
                    }
                }

                var deletedIds = ElementDelta.CreateDeletedListFromChangeSet(elementChangeSet);
                var currentVersionGuid = RevitFacade.GetDocumentVersionGuid(doc);

                // Payload mirrors SubmitElementDeltas so it can be replayed directly against Django.
                var payload = new
                {
                    documentGuid = docGuid,
                    source_state = currentVersionGuid.ToString(),
                    timestamp = DateTime.UtcNow.ToString("o"),
                    revitUser = doc.Application.Username,
                    revitAccountId = doc.Application.LoginUserId,
                    windowsUser = Environment.UserName,
                    machine = Environment.MachineName,
                    elements = elementDeltaList,
                    deletedElements = deletedIds
                };

                Directory.CreateDirectory(outputDir);
                File.WriteAllText(outputPath, Utl.SerializeToJson(payload, null));

                watch.Stop();
                Utl.WriteLog($"PerformIncrementalSyncToFile: Wrote {elementDeltaList.Count} changed + {deletedIds.Count} deleted to {outputPath} in {watch.Elapsed:hh\\:mm\\:ss}");
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex) when (ex.Message.Contains("baseVersionGUID"))
            {
                Utl.WriteLog("PerformIncrementalSyncToFile: Local history missing. Falling back to full sync.");
                FullSyncRunner.PerformFullSyncToFile(revit, outputDir, categories, collectConnectedElements);
            }
            catch (TypeLoadException ex)
            {
                Utl.WriteLog($"PerformIncrementalSyncToFile: Revit API type unavailable on this Revit version ({ex.TypeName}). Falling back to full sync.");
                FullSyncRunner.PerformFullSyncToFile(revit, outputDir, categories, collectConnectedElements);
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"PerformIncrementalSyncToFile failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // Stream-reads just the first "source_state" value from a Perseus JSON without loading
        // the whole file — used by PerformIncrementalSyncToFile to discover the prior baseline.
        private static Guid? ReadFirstSourceState(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                using (var stream = File.OpenRead(filePath))
                using (var textReader = new StreamReader(stream))
                using (var jsonReader = new JsonTextReader(textReader))
                {
                    while (jsonReader.Read())
                    {
                        if (jsonReader.TokenType == JsonToken.PropertyName
                            && string.Equals((string)jsonReader.Value, "source_state", StringComparison.Ordinal))
                        {
                            jsonReader.Read();
                            var raw = jsonReader.Value?.ToString();
                            if (Guid.TryParse(raw, out var guid)) return guid;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"ReadFirstSourceState error: {ex.Message}");
            }
            return null;
        }
    }
}
