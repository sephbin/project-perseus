using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using ProjectPerseus.auth;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.web;

namespace ProjectPerseus.sync
{
    internal class PendingEditItem
    {
        [JsonProperty("id")]          public int    Id             { get; set; }
        [JsonProperty("element_unique_id")] public string ElementUniqueId { get; set; }
        [JsonProperty("param_name")]  public string ParamName      { get; set; }
        [JsonProperty("new_value")]   public string NewValue       { get; set; }
        [JsonProperty("status")]      public string Status         { get; set; }
    }

    internal class PendingEditsApplierResult
    {
        public int Applied { get; set; }
        public int Skipped { get; set; }
        public int Total   { get; set; }
    }

    // Shared logic: fetch pending web edits from Django, apply them to the Revit
    // model (respecting worksharing ownership), then mark applied ones on the server.
    // Used by PullWebEditsCommand (manual ribbon) and SyncOrchestrator (pre-sync prompt).
    internal static class PendingEditsApplier
    {
        public static List<PendingEditItem> Fetch(string docGuid)
        {
            try
            {
                var serverRoot = new Uri(Config.Instance.BaseUrl).GetLeftPart(UriPartial.Authority);
                var endpoint   = $"{serverRoot}/source/{docGuid}/pending-edits/";
                string json    = WebHelper.Get(endpoint, AuthService.GetAuthTokenSafely(), null);
                return JsonConvert.DeserializeObject<List<PendingEditItem>>(json) ?? new List<PendingEditItem>();
            }
            catch (Exception ex)
            {
                Log.Warn($"PendingEditsApplier.Fetch failed: {ex.Message}");
                return new List<PendingEditItem>();
            }
        }

        public static PendingEditsApplierResult Apply(Document doc, string docGuid, List<PendingEditItem> edits)
        {
            var result     = new PendingEditsApplierResult { Total = edits.Count };
            var appliedIds = new List<int>();

            using (var tx = new Transaction(doc, "Perseus: Apply Web Parameter Edits"))
            {
                tx.Start();
                foreach (var edit in edits)
                {
                    try
                    {
                        var element = doc.GetElement(edit.ElementUniqueId);
                        if (element == null)
                        {
                            Log.Warn($"PendingEditsApplier: element not found: {edit.ElementUniqueId}");
                            result.Skipped++;
                            continue;
                        }

                        if (doc.IsWorkshared)
                        {
                            var status = WorksharingUtils.GetCheckoutStatus(doc, element.Id);
                            if (status == CheckoutStatus.OwnedByOtherUser)
                            {
                                Log.Info($"PendingEditsApplier: skipping {edit.ElementUniqueId} — checked out by another user.");
                                result.Skipped++;
                                continue;
                            }
                            if (status == CheckoutStatus.NotOwned)
                                WorksharingUtils.CheckoutElements(doc, new List<ElementId> { element.Id });
                        }

                        var param = element.LookupParameter(edit.ParamName);
                        if (param == null || param.IsReadOnly)
                        {
                            Log.Warn($"PendingEditsApplier: param '{edit.ParamName}' not found or read-only on {edit.ElementUniqueId}.");
                            result.Skipped++;
                            continue;
                        }

                        string val = edit.NewValue ?? "";
                        bool   set = false;

                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                param.Set(val);
                                set = true;
                                break;
                            case StorageType.Double:
                                if (double.TryParse(val, out double d)) { param.Set(d); set = true; }
                                break;
                            case StorageType.Integer:
                                if (int.TryParse(val, out int i)) { param.Set(i); set = true; }
                                break;
                        }

                        if (set) { appliedIds.Add(edit.Id); result.Applied++; }
                        else     { result.Skipped++; }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"PendingEditsApplier: error on edit {edit.Id}: {ex.Message}");
                        result.Skipped++;
                    }
                }
                tx.Commit();
            }

            if (appliedIds.Any())
                MarkApplied(docGuid, appliedIds);

            return result;
        }

        private static void MarkApplied(string docGuid, List<int> ids)
        {
            try
            {
                var serverRoot = new Uri(Config.Instance.BaseUrl).GetLeftPart(UriPartial.Authority);
                var endpoint   = $"{serverRoot}/source/{docGuid}/pending-edits/mark-applied/";
                var payload    = JsonConvert.SerializeObject(new { ids });
                WebHelper.Post(endpoint, AuthService.GetAuthTokenSafely(), payload);
                Log.Info($"PendingEditsApplier: marked {ids.Count} edit(s) applied.");
            }
            catch (Exception ex)
            {
                Log.Warn($"PendingEditsApplier.MarkApplied failed: {ex.Message}");
            }
        }
    }
}
