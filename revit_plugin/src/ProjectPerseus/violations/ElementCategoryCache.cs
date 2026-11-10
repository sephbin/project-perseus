using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Category / name / type of every element, so ViolationDetector can describe elements after they are deleted.
    /// Prime() only queues the document: the cache is filled in time-boxed Idling slices, never in DocumentOpened.
    /// (A blocking prime froze Revit for 20-60 s after opening 300k-740k element models.) Until priming finishes,
    /// Get() returns null for elements not seen yet; callers already handle that.
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
            public readonly Stopwatch Total = new Stopwatch();
        }

        // Main thread only (DocumentOpened / Idling / DocumentClosing).
        private static readonly List<PrimeJob> _jobs = new List<PrimeJob>();

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
        }

        internal static void Prime(Document doc, string docGuid)
        {
            if (doc == null || string.IsNullOrEmpty(docGuid)) return;
            _cache.GetOrAdd(docGuid, _ => new ConcurrentDictionary<long, ElementInfo>());
            _jobs.RemoveAll(j => j.Doc == doc || j.DocGuid == docGuid);
            _jobs.Add(new PrimeJob { Doc = doc, DocGuid = docGuid });
        }

        private static void OnClosing(object sender, DocumentClosingEventArgs e)
        {
            if (e.Document != null) _jobs.RemoveAll(j => j.Doc == e.Document);
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_jobs.Count == 0) return;
            try
            {
                var job = _jobs[0];
                if (job.Doc == null || !job.Doc.IsValidObject) { _jobs.RemoveAt(0); return; }
                if (Step(job, IdleBudgetMs))
                {
                    _jobs.RemoveAt(0);
                    Log.Info($"[ElementCategoryCache] Primed {job.Count} elements for {job.DocGuid} " +
                             $"({job.Total.ElapsedMilliseconds} ms over idle slices)");
                }
                if (_jobs.Count > 0) e.SetRaiseWithoutDelay();
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
                    job.Ids = new List<ElementId>(
                        new FilteredElementCollector(job.Doc).WhereElementIsNotElementType().ToElementIds());
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
                            new ElementInfo(el.Category.Name, el.UniqueId, el.Name ?? "", GetFamilyTypeName(el)));
                        job.Count++;
                    }
                    if ((job.Cursor & 63) == 0 && clock.ElapsedMilliseconds >= budgetMs) return false;
                }
                return true;
            }
            finally { job.Total.Stop(); }
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
