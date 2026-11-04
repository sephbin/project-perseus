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
        // Elements other users changed (from the last sync's change set), waiting for the next pre-sync ID fill.
        private static readonly Dictionary<Document, HashSet<long>> _preSync = new Dictionary<Document, HashSet<long>>();

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

        /// <summary>
        /// Called by incremental sync with the elements other users created or changed since the last sync. Those
        /// never pass through DocumentChanged on this machine, so a colleague without Perseus leaves them without
        /// IDs. They are NOT written now: writing them straight after a sync would check them out to this user until
        /// their next sync, blocking everyone else. They are parked and filled just before this user's next sync
        /// (<see cref="NeedsPreSyncFill"/> / <see cref="RunPreSyncFill"/>), so that sync releases them again.
        /// </summary>
        internal static void QueueFromSync(Document doc, IEnumerable<long> ids)
        {
            if (doc == null || doc.IsFamilyDocument || doc.IsLinked || !IdRulesCache.HasAny) return;
            int n = 0;
            lock (_lock)
            {
                if (!_preSync.TryGetValue(doc, out var set)) _preSync[doc] = set = new HashSet<long>();
                foreach (var id in ids) { set.Add(id); n++; }
            }
            Log.Info($"[IdAutoAssign] incremental sync parked {n} changed element(s) for the next pre-sync ID fill");
        }

        /// <summary>
        /// Read-only check run inside the pre-sync hook: true when parked elements or Django's "assign_id" requests
        /// need an ID written that this user is able to write. The model is not touched.
        /// </summary>
        internal static bool NeedsPreSyncFill(Document doc, string docGuid, IdRulesDto rules)
        {
            if (doc == null || rules == null || IsAssigning) return false;
            var ids = new HashSet<long>();
            lock (_lock) { if (_preSync.TryGetValue(doc, out var set)) ids.UnionWith(set); }
            ids.UnionWith(IdGapFiller.Collect(doc, docGuid, rules).Keys);
            if (ids.Count == 0) return false;

            var plan = IdAssigner.Run(doc, docGuid, rules, ids, dryRun: true);
            if (plan.Planned == 0)
            {
                // Nothing to write that this user can write: forget the parked set so it is not re-checked forever.
                lock (_lock) _preSync.Remove(doc);
                return false;
            }
            Log.Info($"[IdAutoAssign] pre-sync: {plan.Planned} ID write(s) needed on {ids.Count} candidate element(s)");
            return true;
        }

        /// <summary>
        /// Writes the parked and server-requested IDs. Must run on the main thread outside any document event, right
        /// before a sync (IdPreSyncFixEvent, AutoSyncEvent) so the ownership taken is released by that sync.
        /// </summary>
        internal static void RunPreSyncFill(Document doc)
        {
            if (doc == null || !doc.IsValidObject || doc.IsReadOnly || doc.IsModifiable) return;
            string docGuid = ModelGuidStorage.GetOrCreate(doc);
            IdRulesDto rules = IdRulesCache.Get(docGuid);
            if (rules == null) return;

            HashSet<long> ids;
            lock (_lock)
            {
                ids = _preSync.TryGetValue(doc, out var set) ? new HashSet<long>(set) : new HashSet<long>();
                _preSync.Remove(doc);
            }
            try
            {
                EnsureParametersOnce(doc, docGuid, rules);
                if (ids.Count > 0)
                {
                    var run = IdAssigner.Run(doc, docGuid, rules, ids);
                    Log.Info($"[IdAutoAssign] pre-sync fill: {ids.Count} parked element(s), assigned {run.Assigned}, not owned {run.SkippedNotOwned}");
                }
                IdGapFiller.Fulfil(doc, docGuid, rules);
            }
            catch (Exception ex)
            {
                Log.Warn($"[IdAutoAssign] pre-sync fill failed (non-fatal): {ex.Message}");
            }
        }

        // Forget cached state for a document (closing, or Reload Latest brought in other users' edits).
        internal static void Reset(string docGuid)
        {
            IdAssigner.InvalidateIndex(docGuid);
            lock (_lock) _ensured.RemoveWhere(k => k.StartsWith(docGuid + "|", StringComparison.Ordinal));
        }

        // Drops the parked pre-sync set when a document closes.
        internal static void Forget(Document doc)
        {
            if (doc == null) return;
            lock (_lock) { _preSync.Remove(doc); _pending.Remove(doc); }
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (IsAssigning || ViolationDetector.IsSyncInProgress || !IdRulesCache.HasAny) return;
            if (e.Operation != UndoOperation.TransactionCommitted) return;

            Document doc = e.GetDocument();
            // Revit marks the document read-only for the duration of DocumentChanged, so IsReadOnly is always true
            // here; writability is checked in ProcessPending, once the ExternalEvent runs outside the event.
            if (doc == null || doc.IsFamilyDocument || doc.IsLinked) return;
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
                    if (kv.Value.Count > 0) IdAssigner.Run(doc, docGuid, rules, kv.Value);
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
