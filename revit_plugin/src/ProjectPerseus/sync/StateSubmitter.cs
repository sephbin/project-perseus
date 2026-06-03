using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.config;
using ProjectPerseus.models;
using ProjectPerseus.web;

namespace ProjectPerseus.sync
{
    // Thin wrappers over ProjectPerseusWeb. Will relocate to web/ in P5 once
    // ProjectPerseusWeb itself moves out of root.
    internal static class StateSubmitter
    {
        public static void SubmitElementDeltas(IList<ElementDelta> elements, IList<long> deleted, Document doc, string batchId = null)
        {
            new ProjectPerseusWeb(Config.Instance.BaseUrl, Config.Instance.ApiToken)
                .SubmitElementDeltas(elements, deleted, doc, batchId);
        }

        public static void SubmitElementState(IList<ElementDelta> elements, string batchId = null)
        {
            new ProjectPerseusWeb(Config.Instance.BaseUrl, Config.Instance.ApiToken)
                .SubmitElementState(elements, batchId);
        }
    }
}
