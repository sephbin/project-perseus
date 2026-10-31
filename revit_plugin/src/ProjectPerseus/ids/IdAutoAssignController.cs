using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.queue;
using ProjectPerseus.revit;
using ProjectPerseus.violations;

namespace ProjectPerseus.ids
{
    /// <summary>
    /// Watches DocumentChanged for created/modified elements and queues them for ID assignment.
    /// Parameters can't be written inside DocumentChanged, so the work is deferred to an ExternalEvent
    /// (AssignIdsEvent) which calls <see cref="ProcessPending"/> on the main thread.
    /// </summary>
    internal static class IdAutoAssignController
    {
        // True while IdAssigner is writing, so the DocumentChanged its own transaction raises is ignored.
        internal static volatile bool IsAssigning;

        private static ExternalEvent _event;
        private static string _baseUrl;
        private static readonly object _lock = new object();
        private static readonly Dictionary<Document, HashSet<long>> _pending = new Dictionary<Document, HashSet<long>>();
        private static readonly HashSet<string> _ensured = new HashSet<string>();

        internal static void Subscribe(UIControlledApplication application, string baseUrl)
        {
            _baseUrl = baseUrl;
            _event = ExternalEvent.Create(new AssignIdsEvent());
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        }

        internal static void Unsubscribe(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        }

        // Forget cached state for a document (closing, or Reload Latest brought in other users' edits).
        internal static void Reset(string docGuid)
        {
            IdAssigner.InvalidateIndex(docGuid);
            lock (_lock) _ensured.RemoveWhere(k => k.StartsWith(docGuid + "|", StringComparison.Ordinal));
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (IsAssigning || ViolationDetector.IsSyncInProgress || !IdRulesCache.HasAny) return;
            if (e.Operation != UndoOperation.TransactionCommitted) return;

            Document doc = e.GetDocument();
            if (doc == null || doc.IsFamilyDocument || doc.IsLinked || doc.IsReadOnly) return;
            if (e.GetTransactionNames().Any(n => n.StartsWith("Perseus:", StringComparison.Ordinal))) return;

            var ids = e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).Select(i => i.GetIdValue()).ToList();
            if (ids.Count == 0) return;

            lock (_lock)
            {
                if (!_pending.TryGetValue(doc, out var set)) _pending[doc] = set = new HashSet<long>();
                foreach (var id in ids) set.Add(id);
            }
            var raised = _event?.Raise();
            Log.Info($"[IdAutoAssign] queued {ids.Count} changed element(s) from \"{string.Join("|", e.GetTransactionNames())}\"; event {raised}");
        }

        // Called by AssignIdsEvent on the main Revit thread.
        internal static void ProcessPending(UIApplication app)
        {
            Dictionary<Document, HashSet<long>> batch;
            lock (_lock)
            {
                batch = new Dictionary<Document, HashSet<long>>(_pending);
                _pending.Clear();
            }

            foreach (var kv in batch)
            {
                Document doc = kv.Key;
                try
                {
                    if (!doc.IsValidObject || doc.IsReadOnly || doc.IsModifiable)
                    {
                        Log.Info($"[IdAutoAssign] skipped {kv.Value.Count} element(s): document not writable right now (modifiable={doc.IsModifiable}).");
                        continue;
                    }

                    string docGuid = ModelGuidStorage.GetOrCreate(doc);
                    IdRulesDto rules = IdRulesCache.Get(docGuid);
                    if (rules == null)
                    {
                        Log.Info($"[IdAutoAssign] skipped {kv.Value.Count} element(s): no ID rules cached for {docGuid}.");
                        continue;
                    }
                    Log.Info($"[IdAutoAssign] processing {kv.Value.Count} element(s).");

                    EnsureParametersOnce(doc, docGuid, rules);
                    IdAssigner.Run(doc, docGuid, rules, kv.Value);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[IdAutoAssignController] Assignment failed (non-fatal): {ex.Message}");
                }
            }
        }

        // Parameters are added lazily on the first edit rather than at document open, so merely
        // opening a model never modifies it.
        private static void EnsureParametersOnce(Document doc, string docGuid, IdRulesDto rules)
        {
            string key = docGuid + "|" + rules.Signature;
            lock (_lock) { if (_ensured.Contains(key)) return; }
            SharedParameterManager.Ensure(doc, rules);
            lock (_lock) _ensured.Add(key);
        }
    }
}
