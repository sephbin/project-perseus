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
using ProjectPerseus.sync;
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
        private static readonly Dictionary<Document, HashSet<long>> _parked = new Dictionary<Document, HashSet<long>>();
        // Documents whose parked set has not been dry-run yet (PlanParkedAtIdle).
        private static readonly HashSet<Document> _parkedUnplanned = new HashSet<Document>();
        // Documents with edits waiting for the ID index to finish building at idle.
        private static readonly HashSet<Document> _waitingForIndex = new HashSet<Document>();

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
        /// (<see cref="PreSyncFillCount"/> / <see cref="RunPreSyncFill"/>), so that sync releases them again.
        /// </summary>
        internal static void QueueFromSync(Document doc, IEnumerable<long> ids)
        {
            if (doc == null || doc.IsFamilyDocument || doc.IsLinked || !IdRulesCache.HasAny) return;
            int n = 0;
            lock (_lock)
            {
                if (!_parked.TryGetValue(doc, out var set)) _parked[doc] = set = new HashSet<long>();
                foreach (var id in ids) { set.Add(id); n++; }
                _parkedUnplanned.Add(doc);
            }
            Log.Info($"[IdAutoAssign] incremental sync parked {n} changed element(s); they are checked at idle and written before the next sync");
        }

        /// <summary>
        /// Idle-time step (IdIndexMaintainer, once the index is current): dry-runs the parked elements and keeps only
        /// those that really need an ID this user can write. Doing it here means the pre-sync hook only has to look at
        /// the result, never run the rules itself.
        /// </summary>
        internal static void PlanParkedAtIdle(Document doc, string docGuid, IdRulesDto rules)
        {
            HashSet<long> ids;
            lock (_lock)
            {
                if (!_parkedUnplanned.Contains(doc) || !_parked.TryGetValue(doc, out var set)) return;
                _parkedUnplanned.Remove(doc);
                ids = new HashSet<long>(set);
            }
            if (ids.Count == 0 || !IdAssigner.IsIndexReady(docGuid, rules)) return;

            var plan = IdAssigner.Run(doc, docGuid, rules, ids, dryRun: true);
            lock (_lock)
            {
                if (!_parked.TryGetValue(doc, out var set)) return;
                set.IntersectWith(plan.PlannedIds);
                if (set.Count == 0) _parked.Remove(doc);
            }
            Log.Info($"[IdAutoAssign] parked elements checked at idle: {plan.Planned} of {ids.Count} need an ID before the next sync");
        }

        // Most server "assign_id" requests the pre-sync hook will dry-run itself; more wait for a later sync.
        private const int PreSyncRequestLimit = 200;

        /// <summary>
        /// Pre-sync decision, cheap by design: parked elements were already planned at idle; only the server's
        /// "assign_id" requests (already fetched by the caller, usually a handful) are dry-run here, and only when the
        /// ID index is in memory. Read-only.
        /// </summary>
        internal static int PreSyncFillCount(Document doc, string docGuid, IdRulesDto rules, List<PendingEditItem> assignRequests)
        {
            if (doc == null || rules == null || IsAssigning) return 0;
            int parked;
            bool unplanned;
            lock (_lock)
            {
                parked = _parked.TryGetValue(doc, out var set) ? set.Count : 0;
                unplanned = _parkedUnplanned.Contains(doc);
            }
            if (unplanned) Log.Info("[IdAutoAssign] pre-sync: parked elements not checked yet (Revit was not idle); they wait for a later sync");
            int count = unplanned ? 0 : parked;

            if (assignRequests != null && assignRequests.Count > 0 && IdAssigner.IsIndexReady(docGuid, rules))
            {
                var ids = new HashSet<long>();
                foreach (var req in assignRequests.Take(PreSyncRequestLimit))
                {
                    var el = doc.GetElement(req.ElementUniqueId);
                    if (el != null && IdAssigner.IsEligible(el) && IdAssigner.IsApplicable(rules, el)) ids.Add(el.Id.GetIdValue());
                }
                if (ids.Count > 0) count += IdAssigner.Run(doc, docGuid, rules, ids, dryRun: true).Planned;
            }
            return count;
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
                // Unplanned parked elements stay parked for the idle check; planned ones are written now.
                if (_parkedUnplanned.Contains(doc)) ids = new HashSet<long>();
                else
                {
                    ids = _parked.TryGetValue(doc, out var set) ? new HashSet<long>(set) : new HashSet<long>();
                    _parked.Remove(doc);
                }
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

        // Forget cached state for a document (closing, audit, renumber). Not called after a sync any more:
        // IdIndexMaintainer folds other users' changes into the index instead of rebuilding it.
        internal static void Reset(string docGuid)
        {
            IdAssigner.InvalidateIndex(docGuid);
            lock (_lock) _ensured.RemoveWhere(k => k.StartsWith(docGuid + "|", StringComparison.Ordinal));
        }

        // Drops the parked pre-sync set when a document closes.
        internal static void Forget(Document doc)
        {
            if (doc == null) return;
            lock (_lock) { _parked.Remove(doc); _parkedUnplanned.Remove(doc); _pending.Remove(doc); _waitingForIndex.Remove(doc); }
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
                    if (!IdAssigner.IsIndexReady(docGuid, rules))
                    {
                        // A full scan here would freeze the edit for seconds on a big model; the index is being built
                        // at idle and IndexReady() raises this event again when it is done.
                        lock (_lock)
                        {
                            if (!_pending.TryGetValue(doc, out var set)) _pending[doc] = set = new HashSet<long>();
                            set.UnionWith(kv.Value);
                            _waitingForIndex.Add(doc);
                        }
                        Log.Info($"[IdAutoAssign] {kv.Value.Count} element(s) wait for the ID index to finish building.");
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

        /// <summary>Called by IdIndexMaintainer when a document's index has been published.</summary>
        internal static void IndexReady(Document doc)
        {
            bool raise;
            lock (_lock) raise = _waitingForIndex.Remove(doc) && _pending.ContainsKey(doc);
            if (raise) _event?.Raise();
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
