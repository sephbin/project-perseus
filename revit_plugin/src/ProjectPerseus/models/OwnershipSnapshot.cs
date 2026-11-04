using System;
using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>
    /// Who owns what in a workshared model, as Revit's local cache knows it (current as of this user's last sync,
    /// Reload Latest or checkout). Built by ownership/OwnershipScanner; shown by ui/OwnershipForm.
    /// </summary>
    public class OwnershipSnapshot
    {
        public string DocumentTitle;
        public string CurrentUser;
        public DateTime TakenAt;
        public long ElapsedMs;
        public int ElementsScanned;

        // Elements owned individually (borrowed or checked out), not by owning their whole workset.
        public readonly List<ElementRow> Elements = new List<ElementRow>();
        // Worksets with an owner. Owning a workset makes every element in it editable only by that user.
        public readonly List<WorksetRow> Worksets = new List<WorksetRow>();

        public class ElementRow
        {
            public long Id;
            public string UniqueId;
            public string Owner;
            public string Category;
            public string Name;
            public string Workset;
            public bool IsType;
        }

        public class WorksetRow
        {
            public string Name;
            public string Kind;
            public string Owner;
        }
    }
}
