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

using ProjectPerseus.logging;
using ProjectPerseus.util;
namespace ProjectPerseus.sync
{
    // Incremental sync: uses Revit's GetElementChangeSet against the last-known versionGuid
    // (stored server-side for PerformIncrementalSync, or read from the JSON file's first
    // source_state for PerformIncrementalSyncToFile) to push only created/modified/deleted
    // elements. Falls back to FullSyncRunner if local PacCache history is missing.
    public static class IncrementalSyncRunner
    {
        public static void PerformIncrementalSync(RevitFacade revit, string batchId = null)
        {
            try
            {
                var _baseUrl = Config.Instance.BaseUrl;
                var docId = ModelGuidStorage.GetOrCreate(revit.Document);
                Log.Info(docId);
                var StateEndpoint = $"{_baseUrl}/getstate/{docId}";

                string stateJson = WebHelper.Get(StateEndpoint, null, null);
                JObject json = JObject.Parse(stateJson);

                var lastSyncVersionGuid = Guid.Parse(json["value"].ToString());
                Log.Info(lastSyncVersionGuid.ToString());

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
                            Log.Info("Option 'collectConnectedElements' is TRUE. Harvesting references...");

                            HashSet<long> referencedIds = ElementDelta.GetReferencedIds(elementDeltaList);
                            var existingIds = elementDeltaList.Select(x => x.Element.Id).ToHashSet();
                            referencedIds.ExceptWith(existingIds);

                            Log.Info($"Found {referencedIds.Count} additional connected elements.");

                            if (referencedIds.Count > 0)
                            {
                                var connectedDeltas = ElementDelta.CreateListFromIds(referencedIds, revit.Document, docGuid);
                                elementDeltaList.AddRange(connectedDeltas);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Error in CollectConnectedElements logic: {ex.Message}");
                    }

                    var elementDeltaDeletedList = ElementDelta.CreateDeletedListFromChangeSet(elementChangeSet);

                    Log.Info("About to run SubmitElementDeltas");
                    StateSubmitter.SubmitElementDeltas(elementDeltaList, elementDeltaDeletedList, revit.Document, batchId);
                }
                else
                {
                    Log.Info("No changes detected - skipping upload.");
                    Log.Info("No changes detected - skipping upload.");
                }
            }
            // PacCache is gone — Revit can't replay the change set. Fall back to a full sync
            // to re-establish a baseline.
            catch (Autodesk.Revit.Exceptions.ArgumentException ex) when (ex.Message.Contains("baseVersionGUID"))
            {
                Log.Info("WARNING: Local incremental history is missing or broken (PacCache likely cleared).");
                Log.Info("Automatically falling back to PerformFullSync...");
                FullSyncRunner.PerformFullSync(revit, batchId);
            }
            catch (Exception ex)
            {
                Log.Info($"PerformIncrementalSync critically failed: {ex.Message}\n{ex.StackTrace}");
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
                Log.Info($"PerformIncrementalSyncToFile: No valid previous file at {outputPath}. Falling back to full sync.");
                FullSyncRunner.PerformFullSyncToFile(revit, outputDir, categories, collectConnectedElements);
                return;
            }

            Log.Info($"PerformIncrementalSyncToFile: Last source_state = {lastVersionGuid}");

            try
            {
                var watch = Stopwatch.StartNew();

                var elementChangeSet = revit.GetElementChangeSet(lastVersionGuid.Value);

                if (!elementChangeSet.ContainsChanges())
                {
                    Log.Info("PerformIncrementalSyncToFile: No changes detected.");
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
                        Log.Info($"PerformIncrementalSyncToFile: {referencedIds.Count} connected elements added.");
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"PerformIncrementalSyncToFile: Error collecting connected elements: {ex.Message}");
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
                File.WriteAllText(outputPath, JsonUtils.SerializeToJson(payload, null));

                watch.Stop();
                Log.Info($"PerformIncrementalSyncToFile: Wrote {elementDeltaList.Count} changed + {deletedIds.Count} deleted to {outputPath} in {watch.Elapsed:hh\\:mm\\:ss}");
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex) when (ex.Message.Contains("baseVersionGUID"))
            {
                Log.Info("PerformIncrementalSyncToFile: Local history missing. Falling back to full sync.");
                FullSyncRunner.PerformFullSyncToFile(revit, outputDir, categories, collectConnectedElements);
            }
            catch (TypeLoadException ex)
            {
                Log.Info($"PerformIncrementalSyncToFile: Revit API type unavailable on this Revit version ({ex.TypeName}). Falling back to full sync.");
                FullSyncRunner.PerformFullSyncToFile(revit, outputDir, categories, collectConnectedElements);
            }
            catch (Exception ex)
            {
                Log.Info($"PerformIncrementalSyncToFile failed: {ex.Message}\n{ex.StackTrace}");
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
                Log.Info($"ReadFirstSourceState error: {ex.Message}");
            }
            return null;
        }
    }
}
