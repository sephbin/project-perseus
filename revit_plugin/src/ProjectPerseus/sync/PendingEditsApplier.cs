using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using ProjectPerseus.auth;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.revit;
using StorageType = Autodesk.Revit.DB.StorageType;
using ProjectPerseus.web;

namespace ProjectPerseus.sync
{
    internal class PendingEditItem
    {
        [JsonProperty("id")]                 public int    Id               { get; set; }
        [JsonProperty("element_unique_id")]  public string ElementUniqueId  { get; set; }
        [JsonProperty("element_name")]       public string ElementName       { get; set; }
        [JsonProperty("param_name")]         public string ParamName         { get; set; }
        [JsonProperty("new_value")]          public string NewValue          { get; set; }
        [JsonProperty("status")]             public string Status            { get; set; }
        [JsonProperty("edited_by")]          public string EditedBy          { get; set; }
        [JsonProperty("kind")]               public string Kind              { get; set; }   // "value" (default) or "assign_id"

        public bool IsAssignId => string.Equals(Kind, "assign_id", StringComparison.OrdinalIgnoreCase);

        // Populated locally by PendingEditsApplier.EnrichWithRevitValues — not from JSON.
        [JsonIgnore] public string CurrentRevitValue { get; set; }
    }

    internal class PendingEditsApplierResult
    {
        public int Applied { get; set; }
        public int Skipped { get; set; }
        public int Total   { get; set; }
        // One line per edit that was not applied: "<element> · <param>: <reason>".
        public List<string> Problems { get; } = new List<string>();
    }

    // Shared logic: fetch pending web edits from Django, apply them to the Revit
    // model (respecting worksharing ownership), then mark applied ones on the server.
    // Used by PullWebEditsCommand (manual ribbon) and SyncOrchestrator (pre-sync prompt).
    internal static class PendingEditsApplier
    {
        // Web parameter edits for the review dialog. "assign_id" requests are handled silently by IdGapFiller.
        public static List<PendingEditItem> Fetch(string docGuid) => FetchAll(docGuid).Where(e => !e.IsAssignId).ToList();

        internal static List<PendingEditItem> FetchAssignIdRequests(string docGuid) => FetchAll(docGuid, 15000).Where(e => e.IsAssignId).ToList();

        // Web edits and assign_id requests in one call. The pre-sync hook fetches this once, in parallel with its
        // other server calls, with WebHelper.SyncTimeoutMs; an empty list on failure.
        internal static List<PendingEditItem> FetchAll(string docGuid, int timeoutMs = WebHelper.DefaultTimeoutMs)
        {
            try
            {
                var endpoint = $"{Config.Instance.BaseUrl.TrimEnd('/')}/source/{docGuid}/pending-edits/?kinds=all"; // without kinds=all the server withholds assign_id requests (older clients can't handle them)
                string json  = WebHelper.Get(endpoint, AuthService.GetAuthTokenSafely(), null, timeoutMs: timeoutMs);
                return JsonConvert.DeserializeObject<List<PendingEditItem>>(json) ?? new List<PendingEditItem>();
            }
            catch (Exception ex)
            {
                Log.Warn($"PendingEditsApplier.Fetch failed: {ex.Message}");
                return new List<PendingEditItem>();
            }
        }

        // Fills CurrentRevitValue (and corrects ElementName if blank) by reading the
        // live Revit document. Call this after Fetch, before opening the review dialog.
        public static void EnrichWithRevitValues(List<PendingEditItem> edits, Document doc)
        {
            foreach (var edit in edits)
            {
                try
                {
                    var element = doc.GetElement(edit.ElementUniqueId);
                    if (element == null) { edit.CurrentRevitValue = "(not found)"; continue; }

                    if (string.IsNullOrEmpty(edit.ElementName))
                        edit.ElementName = element.Name;

                    var param = element.LookupParameter(edit.ParamName);
                    if (param == null) { edit.CurrentRevitValue = "(param not found)"; continue; }

                    edit.CurrentRevitValue = param.StorageType == StorageType.String
                        ? (param.AsString() ?? "")
                        : (param.AsValueString() ?? param.AsString() ?? "");
                }
                catch
                {
                    edit.CurrentRevitValue = "";
                }
            }
        }

        public static PendingEditsApplierResult Apply(Document doc, string docGuid, List<PendingEditItem> edits)
        {
            var result     = new PendingEditsApplierResult { Total = edits.Count };
            var appliedIds = new List<int>();

            void Skip(PendingEditItem edit, string what, string reason)
            {
                result.Skipped++;
                result.Problems.Add($"{what} · {edit.ParamName}: {reason}");
                Log.Warn($"PendingEditsApplier: edit {edit.Id} ({edit.ElementUniqueId} · {edit.ParamName}) not applied: {reason}");
            }

            // Resolve elements first.
            var targets = new List<(PendingEditItem Edit, Element Element)>();
            foreach (var edit in edits)
            {
                var element = string.IsNullOrEmpty(edit.ElementUniqueId) ? null : doc.GetElement(edit.ElementUniqueId);
                if (element == null) Skip(edit, edit.ElementName ?? edit.ElementUniqueId, "element not found in this model");
                else targets.Add((edit, element));
            }

            // Check out everything in one call, outside the transaction (WorksharingUtils talks to central).
            var editable = new HashSet<ElementId>();
            if (doc.IsWorkshared && targets.Count > 0)
            {
                var want = new List<ElementId>();
                foreach (var t in targets)
                {
                    var st = WorksharingUtils.GetCheckoutStatus(doc, t.Element.Id);
                    if (st == CheckoutStatus.OwnedByCurrentUser) editable.Add(t.Element.Id);
                    else if (st == CheckoutStatus.NotOwned) want.Add(t.Element.Id);
                }
                if (want.Count > 0)
                {
                    try
                    {
                        foreach (var id in WorksharingUtils.CheckoutElements(doc, want)) editable.Add(id);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"PendingEditsApplier: checkout of {want.Count} element(s) failed: {ex.Message}");
                    }
                }
            }

            var setIds = new List<int>();
            using (var tx = new Transaction(doc, "Perseus: Apply Web Parameter Edits"))
            {
                tx.Start();
                foreach (var (edit, element) in targets)
                {
                    string what = string.IsNullOrEmpty(element.Name) ? $"ID {element.Id.GetIdValue()}" : element.Name;
                    try
                    {
                        if (doc.IsWorkshared && !editable.Contains(element.Id))
                        {
                            WorksharingUtils.GetCheckoutStatus(doc, element.Id, out string owner);
                            Skip(edit, what, string.IsNullOrEmpty(owner) || owner == doc.Application.Username
                                ? "could not be checked out (Reload Latest, then try again)"
                                : $"checked out by {owner}");
                            continue;
                        }

                        var param = element.LookupParameter(edit.ParamName);
                        if (param == null) { Skip(edit, what, "parameter not found"); continue; }
                        if (param.IsReadOnly) { Skip(edit, what, "parameter is read-only"); continue; }

                        string val = edit.NewValue ?? "";
                        bool   set = false;

                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                set = param.Set(val);
                                break;
                            case StorageType.Double:
                                // Web values are what the user sees (display units); SetValueString converts them.
                                set = param.SetValueString(val)
                                      || (double.TryParse(val, System.Globalization.NumberStyles.Float,
                                              System.Globalization.CultureInfo.InvariantCulture, out double d) && param.Set(d));
                                break;
                            case StorageType.Integer:
                                set = (int.TryParse(val, out int i) && param.Set(i)) || param.SetValueString(val);
                                break;
                            default:
                                Skip(edit, what, $"{param.StorageType} parameters can't be set from the web");
                                continue;
                        }

                        if (set) setIds.Add(edit.Id);
                        else     Skip(edit, what, $"Revit refused the value '{val}'");
                    }
                    catch (Exception ex)
                    {
                        Skip(edit, what, ex.Message);
                    }
                }

                var status = tx.Commit();
                if (status == TransactionStatus.Committed)
                {
                    appliedIds.AddRange(setIds);
                    result.Applied = setIds.Count;
                }
                else
                {
                    foreach (var _ in setIds) { result.Skipped++; }
                    result.Problems.Add($"Revit did not commit the changes ({status}).");
                    Log.Warn($"PendingEditsApplier: transaction ended {status}; {setIds.Count} edit(s) not applied.");
                }
            }

            if (appliedIds.Any())
                MarkApplied(docGuid, appliedIds);

            return result;
        }

        internal static void MarkApplied(string docGuid, List<int> ids, string status = "applied")
        {
            try
            {
                var endpoint = $"{Config.Instance.BaseUrl.TrimEnd('/')}/source/{docGuid}/pending-edits/mark-applied/";
                var payload  = JsonConvert.SerializeObject(new { ids, status });
                WebHelper.Post(endpoint, AuthService.GetAuthTokenSafely(), payload, timeoutMs: 15000);
                Log.Info($"PendingEditsApplier: marked {ids.Count} edit(s) {status}.");
            }
            catch (Exception ex)
            {
                Log.Warn($"PendingEditsApplier.MarkApplied failed: {ex.Message}");
            }
        }
    }
}
