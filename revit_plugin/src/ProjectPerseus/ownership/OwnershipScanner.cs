using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using Element = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.ownership
{
    /// <summary>
    /// Reads element and workset ownership from Revit's local worksharing cache (no call to central). Elements
    /// that are owned only because their owner holds the whole workset are reported as the workset, not one by one,
    /// so the lists stay about what people borrowed. Read-only; call on the main thread in an API context.
    /// </summary>
    internal static class OwnershipScanner
    {
        /// <summary>
        /// Fast path for the status-bar badge: elements this user owns individually, plus worksets they own.
        /// </summary>
        internal static int CountMine(Document doc, out int myWorksets)
        {
            myWorksets = 0;
            if (doc == null || !doc.IsWorkshared) return 0;

            string me = doc.Application.Username ?? "";
            var worksetOwners = WorksetOwners(doc);
            foreach (var owner in worksetOwners.Values) if (SameUser(owner, me)) myWorksets++;

            int count = 0;
            foreach (ElementId id in AllElements(doc).ToElementIds())
            {
                if (WorksharingUtils.GetCheckoutStatus(doc, id) != CheckoutStatus.OwnedByCurrentUser) continue;
                if (myWorksets > 0 && ViaWorkset(doc, id, me, worksetOwners)) continue;
                count++;
            }
            return count;
        }

        /// <summary>Everything every user owns, for the ownership panel.</summary>
        internal static OwnershipSnapshot Scan(Document doc)
        {
            var clock = Stopwatch.StartNew();
            var snap = new OwnershipSnapshot
            {
                DocumentTitle = doc?.Title ?? "",
                CurrentUser = doc?.Application.Username ?? "",
                TakenAt = DateTime.Now,
            };
            if (doc == null || !doc.IsWorkshared) return snap;

            var worksetOwners = WorksetOwners(doc);
            var worksetNames = new Dictionary<int, string>();
            foreach (Workset ws in new FilteredWorksetCollector(doc).ToWorksets())
            {
                worksetNames[ws.Id.IntegerValue] = ws.Name;
                if (!string.IsNullOrEmpty(ws.Owner))
                    snap.Worksets.Add(new OwnershipSnapshot.WorksetRow { Name = ws.Name, Kind = KindName(ws.Kind), Owner = ws.Owner });
            }

            foreach (ElementId id in AllElements(doc).ToElementIds())
            {
                snap.ElementsScanned++;
                var status = WorksharingUtils.GetCheckoutStatus(doc, id, out string owner);
                if (status == CheckoutStatus.NotOwned || string.IsNullOrEmpty(owner)) continue;
                if (ViaWorkset(doc, id, owner, worksetOwners)) continue;

                Element el = doc.GetElement(id);
                if (el == null) continue;
                worksetNames.TryGetValue(el.WorksetId.IntegerValue, out string wsName);
                snap.Elements.Add(new OwnershipSnapshot.ElementRow
                {
                    Id = id.GetIdValue(),
                    UniqueId = el.UniqueId,
                    Owner = owner,
                    Category = el.Category?.Name ?? (el is ElementType ? "Type" : el.GetType().Name),
                    Name = SafeName(el),
                    Workset = wsName ?? "",
                    IsType = el is ElementType,
                });
            }

            snap.ElapsedMs = clock.ElapsedMilliseconds;
            Log.Info($"[Ownership] scanned {snap.ElementsScanned} elements in {snap.ElapsedMs} ms: " +
                     $"{snap.Elements.Count} owned individually, {snap.Worksets.Count} owned workset(s)");
            return snap;
        }

        // Instances and types: both can be borrowed.
        private static FilteredElementCollector AllElements(Document doc) =>
            new FilteredElementCollector(doc).WherePasses(
                new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true)));

        private static Dictionary<int, string> WorksetOwners(Document doc)
        {
            var owners = new Dictionary<int, string>();
            foreach (Workset ws in new FilteredWorksetCollector(doc).ToWorksets())
                if (!string.IsNullOrEmpty(ws.Owner)) owners[ws.Id.IntegerValue] = ws.Owner;
            return owners;
        }

        // True when the element's owner holds it by owning its workset rather than by borrowing it.
        private static bool ViaWorkset(Document doc, ElementId id, string owner, Dictionary<int, string> worksetOwners)
        {
            if (worksetOwners.Count == 0) return false;
            Element el = doc.GetElement(id);
            if (el == null) return false;
            return worksetOwners.TryGetValue(el.WorksetId.IntegerValue, out string wsOwner) && SameUser(wsOwner, owner);
        }

        internal static bool SameUser(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        private static string SafeName(Element el)
        {
            try
            {
                if (el is ElementType t && !string.IsNullOrEmpty(t.FamilyName)) return t.FamilyName + ": " + t.Name;
                if (el is FamilyInstance fi) return fi.Symbol.FamilyName + ": " + fi.Name;
                return el.Name ?? "";
            }
            catch { return ""; }
        }

        private static string KindName(WorksetKind kind)
        {
            switch (kind)
            {
                case WorksetKind.UserWorkset: return "User workset";
                case WorksetKind.FamilyWorkset: return "Family";
                case WorksetKind.StandardWorkset: return "Project standards";
                case WorksetKind.ViewWorkset: return "View";
                default: return kind.ToString();
            }
        }
    }
}
