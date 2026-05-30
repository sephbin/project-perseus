using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.models;

namespace ProjectPerseus.sync
{
    // Thin wrappers over ProjectPerseusWeb. Will relocate to web/ in P5 once
    // ProjectPerseusWeb itself moves out of root.
    internal static class StateSubmitter
    {
        public static void SubmitElementDeltas(IList<ElementDelta> elements, IList<long> deleted, Document doc)
        {
            new ProjectPerseusWeb(Config.Instance.BaseUrl, Config.Instance.ApiToken)
                .SubmitElementDeltas(elements, deleted, doc);
        }

        public static void SubmitElementState(IList<ElementDelta> elements)
        {
            new ProjectPerseusWeb(Config.Instance.BaseUrl, Config.Instance.ApiToken)
                .SubmitElementState(elements);
        }
    }
}
