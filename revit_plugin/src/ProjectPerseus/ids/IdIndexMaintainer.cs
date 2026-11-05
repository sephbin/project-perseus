using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;

namespace ProjectPerseus.ids
{
    /// <summary>
    /// Keeps IdAssigner's per-document ID index ready, so nothing on the sync path ever scans the whole model.
    ///  - Builds it in small time-boxed slices during Idling (IdAssigner.IndexBuilder) once ID rules are loaded.
    ///  - Records changed/deleted element ids from DocumentChanged (cheap: ids only) and applies them at idle.
    ///  - After a sync or Reload Latest (other users' changes, which raise no DocumentChanged here) asks Revit for
    ///    everything changed since the version the index was last aligned to (Document.GetChangedElements).
    /// The pre-sync hook reads the result through <see cref="TryGetClaims"/>. Main thread only.
    /// </summary>
    internal static class IdIndexMaintainer
    {
        private const int IdleBudgetMs = 40;        // per Idling tick, so Revit stays responsive
        private const int ApplyChunk = 2000;        // changed elements re-read per idle tick
        private const int PreSyncApplyLimit = 5000; // pending changes the pre-sync hook will still apply itself

        private class State
        {
            public Document Doc;
            public readonly HashSet<long> Changed = new HashSet<long>();
            public readonly HashSet<long> Deleted = new HashSet<long>();
            public Guid AlignedVersion = Guid.Empty;  // index reflects the model as of this version + Changed/Deleted
            public bool CatchUp;                      // a sync / Reload Latest brought in changes we were not told about
            public IdAssigner.IndexBuilder Builder;
            public string BuilderSignature;
        }

        private static readonly Dictionary<Document, State> _docs = new Dictionary<Document, State>();

        internal static void Subscribe(UIControlledApplication app)
        {
            app.Idling += OnIdling;
            app.ControlledApplication.DocumentChanged += OnDocumentChanged;
            app.ControlledApplication.DocumentSynchronizedWithCentral += OnSynced;
            app.ControlledApplication.DocumentReloadedLatest += OnReloaded;
            app.ControlledApplication.DocumentClosing += OnClosing;
        }

        internal static void Unsubscribe(UIControlledApplication app)
        {
            app.Idling -= OnIdling;
            app.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            app.ControlledApplication.DocumentSynchronizedWithCentral -= OnSynced;
            app.ControlledApplication.DocumentReloadedLatest -= OnReloaded;
            app.ControlledApplication.DocumentClosing -= OnClosing;
        }

        private static State For(Document doc)
        {
            if (!_docs.TryGetValue(doc, out var st)) _docs[doc] = st = new State { Doc = doc };
            return st;
        }

        private static bool Tracks(Document doc) => doc != null && !doc.IsFamilyDocument && !doc.IsLinked;

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (!Tracks(doc) || !IdRulesCache.HasAny) return;
                var st = For(doc);
                foreach (var id in e.GetAddedElementIds()) st.Changed.Add(id.GetIdValue());
                foreach (var id in e.GetModifiedElementIds()) st.Changed.Add(id.GetIdValue());
                foreach (var id in e.GetDeletedElementIds()) { long v = id.GetIdValue(); st.Deleted.Add(v); st.Changed.Remove(v); }
            }
            catch (Exception ex) { Log.Warn($"[IdIndex] change tracking failed: {ex.Message}"); }
        }

        private static void OnSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            if (Tracks(e.Document) && e.Status == RevitAPIEventStatus.Succeeded) For(e.Document).CatchUp = true;
        }

        private static void OnReloaded(object sender, DocumentReloadedLatestEventArgs e)
        {
            if (Tracks(e.Document) && e.Status == RevitAPIEventStatus.Succeeded) For(e.Document).CatchUp = true;
        }

        private static void OnClosing(object sender, DocumentClosingEventArgs e)
        {
            if (e.Document != null) _docs.Remove(e.Document);
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication app)) return;
            try
            {
                Document doc = app.ActiveUIDocument?.Document;
                if (!Tracks(doc) || !IdRulesCache.HasAny) return;
                string docGuid = ModelGuidStorage.TryGet(doc);
                if (docGuid == null) return;
                var rules = IdRulesCache.Get(docGuid);
                if (rules == null || !rules.Rules.Any(r => r.Enabled)) return;

                var clock = Stopwatch.StartNew();
                bool more = Work(doc, docGuid, rules, For(doc), clock, IdleBudgetMs);
                if (!more) IdAutoAssignController.PlanParkedAtIdle(doc, docGuid, rules);
                if (more) e.SetRaiseWithoutDelay();
            }
            catch (Exception ex)
            {
                Log.Warn($"[IdIndex] idle work failed: {ex.Message}");
            }
        }

        // One budgeted unit of work. True when more remains.
        private static bool Work(Document doc, string docGuid, IdRulesDto rules, State st, Stopwatch clock, int budgetMs)
        {
            if (!IdAssigner.IsIndexReady(docGuid, rules))
            {
                if (st.Builder == null || st.BuilderSignature != rules.Signature)
                {
                    // Changes made while the build runs are kept and applied once it is published.
                    st.AlignedVersion = CurrentVersion(doc);
                    st.CatchUp = false;
                    st.Builder = new IdAssigner.IndexBuilder(doc, docGuid, rules);
                    st.BuilderSignature = rules.Signature;
                    Log.Info($"[IdIndex] building ID index for {doc.Title} in idle slices ({st.Builder.Total} candidate elements)");
                }
                if (!st.Builder.Step(Math.Max(5, budgetMs - (int)clock.ElapsedMilliseconds))) return true;
                st.Builder = null;
                IdAutoAssignController.IndexReady(doc);
            }

            if (st.CatchUp) CatchUp(doc, docGuid, st);

            if (st.Changed.Count > 0 || st.Deleted.Count > 0)
            {
                var changed = st.Changed.Take(ApplyChunk).ToList();
                var deleted = st.Deleted.ToList();
                IdAssigner.ApplyChanges(doc, docGuid, rules, changed, deleted);
                foreach (var id in changed) st.Changed.Remove(id);
                st.Deleted.Clear();
            }
            return st.Changed.Count > 0;
        }

        // Folds in everything Revit says changed since the version the index was aligned to.
        private static void CatchUp(Document doc, string docGuid, State st)
        {
            st.CatchUp = false;
            Guid now = CurrentVersion(doc);
            if (st.AlignedVersion == Guid.Empty || now == st.AlignedVersion) { st.AlignedVersion = now; return; }
            try
            {
                var clock = Stopwatch.StartNew();
                var diff = doc.GetChangedElements(st.AlignedVersion);
                foreach (var id in diff.GetCreatedElementIds()) st.Changed.Add(id.GetIdValue());
                foreach (var id in diff.GetModifiedElementIds()) st.Changed.Add(id.GetIdValue());
                if (diff.AreDeletedElementIdsAvailable)
                    foreach (var id in diff.GetDeletedElementIds()) { long v = id.GetIdValue(); st.Deleted.Add(v); st.Changed.Remove(v); }
                else
                    throw new InvalidOperationException("deleted element ids not available");
                st.AlignedVersion = now;
                Log.Info($"[IdIndex] caught up after sync/reload: {st.Changed.Count} changed, {st.Deleted.Count} deleted ({clock.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                // Version history not available locally: rebuild (in idle slices) rather than trust a stale index.
                Log.Info($"[IdIndex] could not diff since last version ({ex.Message}); rebuilding the index at idle");
                IdAssigner.InvalidateIndex(docGuid);
                st.Changed.Clear();
                st.Deleted.Clear();
            }
        }

        private static Guid CurrentVersion(Document doc)
        {
            try { return Document.GetDocumentVersion(doc)?.VersionGUID ?? Guid.Empty; }
            catch { return Guid.Empty; }
        }

        /// <summary>True when the index is current enough for the pre-sync hook to use without a model scan.</summary>
        internal static bool IsReady(Document doc, string docGuid, IdRulesDto rules) =>
            IdAssigner.IsIndexReady(docGuid, rules) &&
            (!_docs.TryGetValue(doc, out var st) || (!st.CatchUp && st.Changed.Count <= PreSyncApplyLimit));

        /// <summary>
        /// The claims snapshot for the pre-sync check, from memory. Applies the few pending changes first. Null (with
        /// a reason) when the index is still being built or too far behind: the check is then skipped for this sync,
        /// which is recoverable, rather than freezing Revit to scan the model.
        /// </summary>
        internal static List<IdClaimItem> TryGetClaims(Document doc, string docGuid, IdRulesDto rules, out string reason)
        {
            reason = null;
            var st = For(doc);
            if (!IdAssigner.IsIndexReady(docGuid, rules))
            {
                reason = st.Builder != null ? $"ID index still building ({st.Builder.Done}/{st.Builder.Total})" : "ID index not built yet";
                return null;
            }
            if (st.CatchUp) { reason = "ID index catching up after the last sync"; return null; }
            if (st.Changed.Count > PreSyncApplyLimit) { reason = $"{st.Changed.Count} changes not yet indexed"; return null; }

            var clock = Stopwatch.StartNew();
            if (st.Changed.Count > 0 || st.Deleted.Count > 0)
            {
                IdAssigner.ApplyChanges(doc, docGuid, rules, st.Changed.ToList(), st.Deleted.ToList());
                st.Changed.Clear();
                st.Deleted.Clear();
            }
            var claims = IdAssigner.SnapshotFromIndex(docGuid, rules);
            Log.Info($"[IdIndex] claims snapshot from memory: {claims?.Count ?? 0} value(s) in {clock.ElapsedMilliseconds} ms");
            return claims;
        }

        /// <summary>
        /// Outside any sync (fix event, auto-sync): brings the index fully up to date, building it now if it must.
        /// May take seconds on a large model the first time; never call from the pre-sync hook.
        /// </summary>
        internal static List<IdClaimItem> GetClaimsBlocking(Document doc, string docGuid, IdRulesDto rules)
        {
            var st = For(doc);
            var clock = Stopwatch.StartNew();
            if (st.CatchUp && IdAssigner.IsIndexReady(docGuid, rules)) CatchUp(doc, docGuid, st);
            while (Work(doc, docGuid, rules, st, Stopwatch.StartNew(), 1000)) { }
            var claims = IdAssigner.SnapshotFromIndex(docGuid, rules) ?? IdAssigner.Snapshot(doc, rules);
            Log.Info($"[IdIndex] claims snapshot (blocking): {claims.Count} value(s) in {clock.ElapsedMilliseconds} ms");
            return claims;
        }
    }
}
