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

        // Pre-serialised elements (collected once with progress), with upload progress per chunk on the status card.
        internal static void SubmitElementPayload(ElementPayload payload, IList<long> deleted, Document doc, string batchId)
        {
            int total = payload.Elements.Count;
            SyncProgressReporter.Uploading(0, 1, total);
            new ProjectPerseusWeb(Config.Instance.BaseUrl, Config.Instance.ApiToken)
                .SubmitElementPayload(payload, deleted, doc, batchId, (sent, chunks) => SyncProgressReporter.Uploading(sent, chunks, total));
        }

    }
}
