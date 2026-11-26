using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.queue;
using ProjectPerseus.revit;
using ProjectPerseus.violations;

namespace ProjectPerseus.collections
{
    /// <summary>
    /// Keeps collection lists (CollectionLists) current while this user edits: DocumentChanged only records the changed ids
    /// (no element reads in the event); the update runs through queue/OwnershipUiEvent once the document has settled, in its
    /// own transaction ("Perseus: Update collection lists", ignored here so it doesn't loop). Owners this user doesn't own
    /// are borrowed like any edit (released at their next sync); owners checked out by others are skipped. Other users'
    /// edits arriving by sync are left to their own Perseus (Audit catches the rest).
    /// </summary>
    internal static class CollectionListController
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<Document, HashSet<long>> _pending = new Dictionary<Document, HashSet<long>>();
        private static bool _scheduled;

        internal static void Subscribe(UIControlledApplication app) =>
            app.ControlledApplication.DocumentChanged += OnDocumentChanged;

        internal static void Unsubscribe(UIControlledApplication app) =>
            app.ControlledApplication.DocumentChanged -= OnDocumentChanged;

        internal static void Forget(Document doc)
        {
            if (doc == null) return;
            lock (_lock) _pending.Remove(doc);
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                if (ViolationDetector.IsSyncInProgress || e.Operation != UndoOperation.TransactionCommitted) return;
                var doc = e.GetDocument();
                if (doc == null || doc.IsFamilyDocument || doc.IsLinked) return;
                if (e.GetTransactionNames().Any(n => n == CollectionLists.TransactionName)) return;
                if (CollectionLists.WithLists(doc).Count == 0) return;
                var ids = e.GetModifiedElementIds().Concat(e.GetAddedElementIds()).Select(i => i.GetIdValue()).ToList();
                if (ids.Count == 0) return;
                bool schedule;
                lock (_lock)
                {
                    if (!_pending.TryGetValue(doc, out var set)) _pending[doc] = set = new HashSet<long>();
                    set.UnionWith(ids);
                    schedule = !_scheduled;
                    _scheduled = true;
                }
                if (schedule) OwnershipUiEvent.Run(Process);
            }
            catch (Exception ex) { Log.Warn($"[CollectionLists] change tracking failed (non-fatal): {ex.Message}"); }
        }

        private static void Process(UIApplication app)
        {
            Dictionary<Document, HashSet<long>> batch;
            lock (_lock)
            {
                batch = new Dictionary<Document, HashSet<long>>(_pending);
                _pending.Clear();
                _scheduled = false;
            }
            foreach (var kv in batch)
            {
                var doc = kv.Key;
                try
                {
                    if (!doc.IsValidObject || doc.IsReadOnly || doc.IsModifiable || !DocumentSettle.IsSettled(doc))
                    {
                        Requeue(doc, kv.Value);
                        continue;
                    }
                    CollectionLists.Refresh(doc, kv.Value);
                }
                catch (Exception ex) { Log.Warn($"[CollectionLists] update failed (non-fatal): {ex.Message}"); }
            }
        }

        // Not writable / settling right now (saving, syncing): try again on the next change.
        private static void Requeue(Document doc, HashSet<long> ids)
        {
            if (!doc.IsValidObject) return;
            lock (_lock)
            {
                if (!_pending.TryGetValue(doc, out var set)) _pending[doc] = set = new HashSet<long>();
                set.UnionWith(ids);
            }
        }
    }
}
