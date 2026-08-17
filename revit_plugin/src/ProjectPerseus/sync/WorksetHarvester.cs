using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ProjectPerseus.sync
{
    // Enumerates all worksets from a workshared Revit document.
    // Returns an empty sequence for non-workshared documents.
    internal static class WorksetHarvester
    {
        public static IEnumerable<Workset> GetAllWorksets(Document doc)
        {
            if (!doc.IsWorkshared) yield break;

            foreach (Workset ws in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                yield return ws;
        }
    }
}
