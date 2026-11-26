using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ProjectPerseus.logging;
using ProjectPerseus.revit;

namespace ProjectPerseus.violations
{
    internal class ElementInfo
    {
        public string CategoryName  { get; }
        public string UniqueId      { get; }
        public string Name          { get; }
        public string FamilyTypeName { get; }
        public ElementInfo(string categoryName, string uniqueId, string name, string familyTypeName = "")
        {
            CategoryName   = categoryName;
            UniqueId       = uniqueId;
            Name           = name ?? "";
            FamilyTypeName = familyTypeName ?? "";
        }
    }

    /// <summary>
    /// Category / name / type of elements, so ViolationDetector can describe elements after they are deleted.
    /// Prime() only covers what the violation rules PROTECT (ViolationSettings.ProtectedCategories + ProtectedElementIds:
    /// all ViolationDetector's protection checks need). Everything else is cached when it changes (Track(), from
    /// ViolationDetector at idle); an untouched unprotected element that is deleted is logged by ElementId only (the
    /// server has its name / category from the synced elements). Re-primed when the protection settings change.
    /// Whole-model priming (~400k elements, back-to-back Idling slices for minutes after opening) was dropped on
    /// 2026-11-26 while chasing crashes during a Rhino.Inside door-flip script; those crashes later proved to be
    /// Rhino.Inside's own (also with Perseus off), but the protected-only prime is far lighter (~4.7k elements, < 0.5 s).
    /// Filled in time-boxed Idling slices, never in DocumentOpened; waits for DocumentSettle; abandoned on save / sync /
    /// reload (Element.Name crashed Revit after cloud saves on 2026-11-25) and re-queued with the next settings change.
    /// </summary>
    internal static class ElementCategoryCache
    {
        private const int IdleBudgetMs = 40;

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<long, ElementInfo>> _cache
            = new ConcurrentDictionary<string, ConcurrentDictionary<long, ElementInfo>>();

        private class PrimeJob
        {
            public Document Doc;
            public string DocGuid;
            public IList<ElementId> Ids;   // collected on the first slice
            public int Cursor;
            public int Count;
            public int Epoch;              // DocumentSettle.Epoch when queued; any change abandons the job
            public string Signature;       // protection settings this job covers
            public DateTime QueuedAt = DateTime.UtcNow;
            public readonly Stopwatch Total = new Stopwatch();
        }

        // Settings not loaded this long after Prime(): give up until they change (checked at idle).
        private static readonly TimeSpan SettingsWait = TimeSpan.FromMinutes(2);

        // Main thread only (DocumentOpened / Idling / DocumentClosing).
        private static readonly List<PrimeJob> _jobs = new List<PrimeJob>();
        // Open documents and the protection signature last primed (null = not primed yet).
        private static readonly Dictionary<string, KeyValuePair<Document, string>> _docs =
            new Dictionary<string, KeyValuePair<Document, string>>();
        private static DateTime _lastSettingsCheck = DateTime.MinValue;

        private static string SignatureOf(ViolationSettings s) => s == null ? null :
            string.Join("\u001f", s.ProtectedCategories.OrderBy(c => c, StringComparer.Ordinal)) + "\u001e" +
            string.Join("\u001f", s.ProtectedElementIds.OrderBy(c => c, StringComparer.Ordinal));

        internal static void Subscribe(UIControlledApplication app)
        {
            app.Idling += OnIdling;
            app.ControlledApplication.DocumentClosing += OnClosing;
        }

        internal static void Unsubscribe(UIControlledApplication app)
        {
            app.Idling -= OnIdling;
            app.ControlledApplication.DocumentClosing -= OnClosing;
            _jobs.Clear();
            _docs.Clear();
        }

        internal static void Prime(Document doc, string docGuid)
        {
            if (doc == null || string.IsNullOrEmpty(docGuid)) return;
            _cache.GetOrAdd(docGuid, _ => new ConcurrentDictionary<long, ElementInfo>());
            _docs[docGuid] = new KeyValuePair<Document, string>(doc, null);
            Queue(doc, docGuid);
        }

        private static void Queue(Document doc, string docGuid)
        {
            _jobs.RemoveAll(j => j.Doc == doc || j.DocGuid == docGuid);
            _jobs.Add(new PrimeJob { Doc = doc, DocGuid = docGuid, Epoch = DocumentSettle.Epoch(doc) });
        }

        private static void OnClosing(object sender, DocumentClosingEventArgs e)
        {
            if (e.Document == null) return;
            _jobs.RemoveAll(j => j.Doc == e.Document);
            foreach (var key in _docs.Where(kv => kv.Value.Key == e.Document).Select(kv => kv.Key).ToList())
                _docs.Remove(key);
        }

        // Every 5 s: a document whose protection settings differ from what was primed is queued again (settings are
        // reloaded after each sync; this also restarts a prime abandoned by a save / sync).
        private static void RequeueChangedSettings()
        {
            if ((DateTime.UtcNow - _lastSettingsCheck).TotalSeconds < 5) return;
            _lastSettingsCheck = DateTime.UtcNow;
            foreach (var kv in _docs.ToList())
            {
                var doc = kv.Value.Key;
                if (doc == null || !doc.IsValidObject) { _docs.Remove(kv.Key); continue; }
                if (_jobs.Any(j => j.DocGuid == kv.Key)) continue;
                string sig = SignatureOf(ViolationSettingsCache.Get(kv.Key));
                if (sig != null && sig != kv.Value.Value) Queue(doc, kv.Key);
            }
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                RequeueChangedSettings();
                if (_jobs.Count == 0) return;
                _jobs.RemoveAll(j => j.Doc == null || !j.Doc.IsValidObject);
                // Saved / synced / reloaded since priming started: stop (see the class summary).
                _jobs.RemoveAll(j =>
                {
                    if (j.Epoch == DocumentSettle.Epoch(j.Doc)) return false;
                    Log.Info($"[ElementCategoryCache] {j.DocGuid}: model saved / synced / reloaded during priming; " +
                             $"stopped after {j.Count} elements (later changes are still tracked)");
                    return true;
                });
                var job = _jobs.Find(j => DocumentSettle.IsSettled(j.Doc));
                if (job == null) return;
                if (Step(job, IdleBudgetMs))
                {
                    _jobs.Remove(job);
                    if (job.Signature != null)
                    {
                        if (_docs.ContainsKey(job.DocGuid))
                            _docs[job.DocGuid] = new KeyValuePair<Document, string>(job.Doc, job.Signature);
                        Log.Info($"[ElementCategoryCache] Primed {job.Count} protected element(s) for {job.DocGuid} " +
                                 $"({job.Ids?.Count ?? 0} collected, {job.Total.ElapsedMilliseconds} ms over idle slices)");
                    }
                }
                // No SetRaiseWithoutDelay: Revit's normal idle cadence (see the class summary).
            }
            catch (Exception ex)
            {
                Log.Warn($"[ElementCategoryCache] idle prime failed: {ex.Message}");
                if (_jobs.Count > 0) _jobs.RemoveAt(0);
            }
        }

        // One budgeted slice. True when the job is finished.
        private static bool Step(PrimeJob job, int budgetMs)
        {
            var clock = Stopwatch.StartNew();
            job.Total.Start();
            try
            {
                if (job.Ids == null)
                {
                    var settings = ViolationSettingsCache.Get(job.DocGuid);
                    if (settings == null)
                    {
                        // Loaded asynchronously after DocumentOpened; RequeueChangedSettings retries when they arrive.
                        return DateTime.UtcNow - job.QueuedAt > SettingsWait;
                    }
                    job.Signature = SignatureOf(settings);
                    job.Ids = CollectProtected(job.Doc, settings);
                    return false;
                }
                if (!_cache.TryGetValue(job.DocGuid, out var map)) return true;
                while (job.Cursor < job.Ids.Count)
                {
                    Element el = job.Doc.GetElement(job.Ids[job.Cursor++]);
                    if (el?.Category != null)
                    {
                        // TryAdd: an entry from Track() (DocumentChanged) is newer than this snapshot.
                        map.TryAdd(el.Id.GetIdValue(),
                            new ElementInfo(el.Category.Name, el.UniqueId, ElementNames.Of(el), GetFamilyTypeName(el)));
                        job.Count++;
                    }
                    if ((job.Cursor & 63) == 0 && clock.ElapsedMilliseconds >= budgetMs) return false;
                }
                return true;
            }
            finally { job.Total.Stop(); }
        }

        // Instances in the protected categories (matched by name, as ViolationDetector compares them) + the protected
        // elements given by ElementId or UniqueId.
        private static IList<ElementId> CollectProtected(Document doc, ViolationSettings settings)
        {
            var ids = new HashSet<ElementId>();
            var wanted = new HashSet<string>(settings.ProtectedCategories, StringComparer.Ordinal);
            if (wanted.Count > 0)
            {
                var catIds = new List<ElementId>();
                foreach (Category c in doc.Settings.Categories)
                    if (c != null && wanted.Contains(c.Name)) catIds.Add(c.Id);
                if (catIds.Count > 0)
                    foreach (var id in new FilteredElementCollector(doc)
                                 .WherePasses(new ElementMulticategoryFilter(catIds))
                                 .WhereElementIsNotElementType().ToElementIds())
                        ids.Add(id);
            }
            foreach (var raw in settings.ProtectedElementIds)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                Element el = long.TryParse(raw.Trim(), out long n) ? doc.GetElement(RevitExtensions.CreateId(n))
                                                                   : doc.GetElement(raw.Trim());
                if (el != null) ids.Add(el.Id);
            }
            return new List<ElementId>(ids);
        }

        internal static void Track(string docGuid, long elementId,
            string categoryName, string uniqueId, string name, string familyTypeName = "")
        {
            if (string.IsNullOrEmpty(docGuid)) return;
            _cache.GetOrAdd(docGuid, _ => new ConcurrentDictionary<long, ElementInfo>())
                  [elementId] = new ElementInfo(categoryName, uniqueId, name, familyTypeName);
        }

        internal static ElementInfo Get(string docGuid, long elementId) =>
            _cache.TryGetValue(docGuid, out var map) && map.TryGetValue(elementId, out var info)
                ? info : null;

        internal static string GetFamilyTypeName(Element el)
        {
            try
            {
                var typeId = el.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var typeEl = el.Document.GetElement(typeId);
                    if (typeEl != null) return typeEl.Name ?? "";
                }
            }
            catch { }
            return "";
        }
    }
}
