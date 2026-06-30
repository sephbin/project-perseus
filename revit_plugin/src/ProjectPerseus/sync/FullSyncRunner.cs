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
        public static void PerformFullSync(RevitFacade revit, string batchId = null)
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

                var rawCategories = json["source"]["parameter_dict"]["perseusCategories"]?.ToObject<List<string>>() ?? new List<string>();
                var categories = rawCategories.FindAll(c => !string.IsNullOrWhiteSpace(c));
                if (categories.Count == 0)
                {
                    Log.Info("PerformFullSync: perseusCategories is empty — skipping.");
                    return;
                }

                // Pre-fetch Django's current element_ids for this source so we can compute
                // ghost deletions inline. As we walk every Revit element (and every
                // synthesized payload entry — categories, connected) we .Remove() its id.
                // Anything still in the set after the walk is in Django but no longer in
                // Revit, and gets sent in the payload as a deletion.
                var ghostCandidates = new HashSet<string>();
                try
                {
                    var existingIdsEndpoint = $"{baseUrl}/sourceelements/{thisdocGuid}/";
                    string existingIdsResponse = WebHelper.Get(existingIdsEndpoint, AuthService.GetAuthTokenSafely(), null);
                    JObject existingIdsJson = JObject.Parse(existingIdsResponse);
                    var djangoElementIds = existingIdsJson["element_ids"]?.ToObject<List<string>>() ?? new List<string>();
                    ghostCandidates = new HashSet<string>(djangoElementIds);
                    Log.Info($"PerformFullSync: pre-fetched {ghostCandidates.Count} existing element ids from Django.");
                }
                catch (Exception ex)
                {
                    Log.Error($"PerformFullSync: failed to pre-fetch Django element ids: {ex.Message}");
                }

                var elements = revit.GetAllElements();
                Log.Info($"PerformFullSync: Found {elements.Count} elements");

                // Log per-category counts from the raw (pre-filter) collection so we can
                // diagnose elements that are visible in Revit but absent from the payload.
                try
                {
                    var rawCategoryCounts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in elements)
                    {
                        var catName = e.CategoryName?.Name ?? "<null>";
                        rawCategoryCounts.TryGetValue(catName, out var c);
                        rawCategoryCounts[catName] = c + 1;
                    }
                    var top = string.Join(", ", rawCategoryCounts
                        .OrderByDescending(kv => kv.Value)
                        .Take(20)
                        .Select(kv => $"{kv.Key}:{kv.Value}"));
                    Log.Debug($"PerformFullSync: raw category counts (top 20): {top}");
                }
                catch (Exception ex)
                {
                    Log.Info($"PerformFullSync: category count log failed: {ex.Message}");
                }

                // Watch-list checkpoint 1: raw GetAllElements collection.
                if (ElementWatchList.Ids.Count > 0)
                {
                    var rawIdSet = new HashSet<long>(elements.Select(e => e.Id.Value));
                    foreach (var wid in ElementWatchList.Ids)
                        Log.Debug($"FullSync watch [1-GetAllElements]: id={wid} present={rawIdSet.Contains(wid)}");
                }

                // Drop every Revit element id from the ghost set BEFORE category filtering
                // so excluded-by-filter elements aren't mis-marked as deleted on next sync.
                foreach (var e in elements)
                {
                    ghostCandidates.Remove(e.Id.Value.ToString());
                }

                var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);
                Log.Info(docGuid);

                var elementDeltaList = ElementDelta.CreateList(ElementDelta.DeltaAction.Create, elements, revit.Document, docGuid).ToList();
                Log.Info("PerformFullSync: Created elementDeltaList");

                // Watch-list checkpoint 2: after ElementDelta.CreateList.
                if (ElementWatchList.Ids.Count > 0)
                {
                    var deltaIdSet = new HashSet<long>(elementDeltaList.Select(d => d.Element.Id));
                    foreach (var wid in ElementWatchList.Ids)
                        Log.Debug($"FullSync watch [2-CreateList]: id={wid} present={deltaIdSet.Contains(wid)}");
                }

                var filteredElementDeltaList = new List<ElementDelta>();

                // Watch-list: log the exact CategoryName string and whether it appears in
                // the perseusCategories list BEFORE the filter runs, so we can see exactly
                // why a watched element is dropped.
                if (ElementWatchList.Ids.Count > 0)
                {
                    Log.Debug($"FullSync watch [pre-CategoryFilter]: perseusCategories=[{string.Join(", ", categories.Take(30))}]");
                    foreach (var wid in ElementWatchList.Ids)
                    {
                        var wd = elementDeltaList.FirstOrDefault(d => d.Element.Id == wid);
                        if (wd != null)
                        {
                            var wcat = wd.Element?.originalElement?.CategoryName?.Name ?? "<null>";
                            var inSet = categories.Any(c => string.Equals(c, wcat, StringComparison.OrdinalIgnoreCase));
                            Log.Debug($"FullSync watch [pre-CategoryFilter]: id={wid} CategoryName='{wcat}' inCategorySet={inSet}");
                        }
                    }
                }

                try { filteredElementDeltaList = elementDeltaList.FilterByCategoryName(categories).ToList(); }
                catch (Exception ex) { Log.Info(ex.ToString()); }

                // Watch-list checkpoint 3: after category filter.
                if (ElementWatchList.Ids.Count > 0)
                {
                    var filtIdSet = new HashSet<long>(filteredElementDeltaList.Select(d => d.Element.Id));
                    foreach (var wid in ElementWatchList.Ids)
                        Log.Debug($"FullSync watch [3-CategoryFilter]: id={wid} present={filtIdSet.Contains(wid)}");
                }

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
                    Log.Info("Harvesting Worksets...");
                    var worksetDeltas = new List<ElementDelta>();

                    foreach (Workset ws in WorksetHarvester.GetAllWorksets(revit.Document))
                    {
                        var wsAdapter = new ProjectPerseus.revit.adapters.ArdbWorksetAdapter(ws);
                        var delta = new ElementDelta(ElementDelta.DeltaAction.Update, wsAdapter, revit.Document, docGuid);
                        worksetDeltas.Add(delta);
                    }

                    Log.Info($"Added {worksetDeltas.Count} Worksets to the payload.");
                    filteredElementDeltaList.AddRange(worksetDeltas);
                }
                catch (Exception ex)
                {
                    Log.Info($"Error harvesting worksets: {ex.Message}");
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

                // Watch-list checkpoint 4: final payload (categories + connected elements added).
                if (ElementWatchList.Ids.Count > 0)
                {
                    var finalIdSet = new HashSet<long>(filteredElementDeltaList.Select(d => d.Element.Id));
                    foreach (var wid in ElementWatchList.Ids)
                        Log.Debug($"FullSync watch [4-FinalPayload]: id={wid} present={finalIdSet.Contains(wid)}");
                }

                // Final ghost sweep: categories and connected (type) elements aren't in
                // `elements` (GetAllElements excludes types), so drop their ids too. Whatever
                // remains in ghostCandidates after this is in Django but not in any payload
                // entry — a true deletion.
                foreach (var d in filteredElementDeltaList)
                {
                    if (d.Element != null) ghostCandidates.Remove(d.Element.Id.ToString());
                }

                var ghostIds = new List<long>();
                foreach (var id in ghostCandidates)
                {
                    if (long.TryParse(id, out var parsed)) ghostIds.Add(parsed);
                }
                Log.Info($"PerformFullSync: {ghostIds.Count} ghost ids (in Django, not in current Revit model).");

                // Snapshot the entire un-chunked payload to %AppData%\ProjectPerseus\
                // fullsync-dumps\ for offline inspection. Shape matches SubmitElementDeltas
                // so the file can be replayed against /add_to_crud_queue/ if needed. Files
                // older than 2 days are pruned in the same pass.
                try
                {
                    var dumpsFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "ProjectPerseus", "fullsync-dumps");
                    Directory.CreateDirectory(dumpsFolder);

                    var cutoff = DateTime.Now.AddDays(-2);
                    foreach (var oldFile in Directory.GetFiles(dumpsFolder, "*-fullsync.json"))
                    {
                        try { if (File.GetLastWriteTime(oldFile) < cutoff) File.Delete(oldFile); }
                        catch { }
                    }

                    string safeDocName = string.Concat(doc.Title.Split(Path.GetInvalidFileNameChars()));
                    string dumpPath = Path.Combine(dumpsFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeDocName}-fullsync.json");

                    var dumpPayload = new
                    {
                        documentGuid = thisdocGuid,
                        source_state = RevitFacade.GetDocumentVersionGuid(doc).ToString(),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        revitUser = app.Username,
                        revitAccountId = app.LoginUserId,
                        windowsUser = Environment.UserName,
                        machine = Environment.MachineName,
                        elements = filteredElementDeltaList,
                        deletedElements = ghostIds
                    };

                    File.WriteAllText(dumpPath, JsonUtils.SerializeToJson(dumpPayload, null));
                    Log.Info($"PerformFullSync: wrote full payload snapshot to {dumpPath}");
                }
                catch (Exception ex)
                {
                    Log.Error($"PerformFullSync: full payload snapshot failed: {ex.Message}");
                }

                // Sample payload: serialize the first non-category element (indented for
                // readability) so we can verify on disk what shape the wire payload actually
                // takes per element — especially synthetic params like "Is FamilySymbol",
                // "Type Id", etc. Logs to the per-session %AppData% log via Log.Info.
                try
                {
                    var sample = filteredElementDeltaList.FirstOrDefault();
                    if (sample != null)
                    {
                        var prettySample = JsonConvert.SerializeObject(
                            sample,
                            Formatting.Indented,
                            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                        Log.Debug($"PerformFullSync: payload sample (1 of {filteredElementDeltaList.Count}):\n{prettySample}");
                    }
                    else
                    {
                        Log.Info("PerformFullSync: payload sample skipped — list is empty.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"PerformFullSync: payload sample log failed: {ex.Message}");
                }

                // Full sync and ghost deletions go through the same endpoint as incremental.
                StateSubmitter.SubmitElementDeltas(filteredElementDeltaList, ghostIds, revit.Document, batchId);

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

                try
                {
                    foreach (Workset ws in WorksetHarvester.GetAllWorksets(doc))
                    {
                        var wsAdapter = new ProjectPerseus.revit.adapters.ArdbWorksetAdapter(ws);
                        elementDeltaList.Add(new ElementDelta(ElementDelta.DeltaAction.Update, wsAdapter, doc, docGuid));
                    }
                    Log.Info($"PerformFullSyncToFile: Added worksets, total {elementDeltaList.Count} items");
                }
                catch (Exception ex)
                {
                    Log.Info($"PerformFullSyncToFile: Error harvesting worksets: {ex.Message}");
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
                Log.Info($"PerformFullSyncToFile: Serializing {elementDeltaList.Count} items to {outputPath}...");
                JsonUtils.WriteToFile(elementDeltaList, outputPath);

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
