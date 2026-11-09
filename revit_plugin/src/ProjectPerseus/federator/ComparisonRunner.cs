using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.ui;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// One comparison end to end between two versions of a linked model ("from" → "to"; each the loaded link, a
    /// cached version or an archived file): snapshot both (SnapshotCache / VersionCatalog first, else SnapshotBuilder
    /// on the loaded link or OldModelOpener on the file), diff them (LinkDiff) and save the result in the host model
    /// (ComparisonStore). Every snapshot taken is cached and recorded in VersionCatalog, so that version shows as ✓
    /// and is never opened again. Progress goes to ui/SyncStatusToast, which paints on its own thread while this
    /// blocks the UI thread. Needs a Revit API context with no open transaction (a command or an ExternalEvent).
    /// </summary>
    internal static class ComparisonRunner
    {
        /// <summary>Below this share of old elements still present, the file probably isn't an earlier version.</summary>
        internal const double MinMatchedShare = 0.2;

        internal class Result
        {
            internal ComparisonRecord Record;
            /// <summary>Set when the comparison ran but looks wrong (e.g. a different model was picked).</summary>
            internal string Warning;
        }

        /// <summary>A version to compare: the loaded link, a cached version, or an archived file.</summary>
        internal class Source
        {
            internal enum SourceKind { Live, Cached, File }
            internal SourceKind Kind;
            internal LinkVersion Version;   // Cached
            internal string Path;           // File
            internal string Label;          // as shown in the picker (without the ✓)

            internal static Source Live(string label) => new Source { Kind = SourceKind.Live, Label = label };
            internal static Source Cached(LinkVersion v, string label) => new Source { Kind = SourceKind.Cached, Version = v, Label = label };
            internal static Source File(string path, string label) => new Source { Kind = SourceKind.File, Path = path, Label = label };
        }

        internal static Result Run(UIApplication uiapp, Document host, RevitLinkInstance instance, string modelKey,
                                   Source from, Source to, DiffOptions options = null)
        {
            options = options ?? new DiffOptions();
            if (from == null || to == null) throw new InvalidOperationException("Pick both versions to compare.");
            Document linkDoc = null;
            try { linkDoc = instance.GetLinkDocument(); } catch { /* unloaded */ }
            if ((from.Kind == Source.SourceKind.Live || to.Kind == Source.SourceKind.Live) && linkDoc == null)
                throw new InvalidOperationException("The link isn't loaded. Reload it, then compare.");
            foreach (var s in new[] { from, to })
                if (s.Kind == Source.SourceKind.File && linkDoc != null &&
                    string.Equals(SafeFullPath(s.Path), SafeFullPath(linkDoc.PathName), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("That's the file the link is loaded from. Pick \"Live\" for it instead.");

            string linkTitle = linkDoc?.Title ?? from.Label;
            try
            {
                string title = "Comparing " + linkTitle;
                // Bar: "from" 0–48 %, "to" 48–95 %, compare + save the rest.
                var oldSnap = SnapshotOf(uiapp, linkDoc, modelKey, from, "From " + from.Label, title, 0.0, 0.48);
                var newSnap = SnapshotOf(uiapp, linkDoc, modelKey, to, "To " + to.Label, title, 0.48, 0.95);
                if (!string.IsNullOrEmpty(oldSnap.VersionGuid) &&
                    string.Equals(oldSnap.VersionGuid, newSnap.VersionGuid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Both picks are the same version of the model (same save). Pick two different versions.");

                SyncStatusToast.Progress(title, 0.96, "Comparing and finding likely replacements…");
                var changes = LinkDiff.Compare(oldSnap, newSnap, options);
                ReplacementMatcher.Suggest(changes);
                LogBreakdown(changes);

                var rec = new ComparisonRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    LinkInstanceUid = instance.UniqueId,
                    LinkTitle = linkTitle,
                    LinkPath = linkDoc?.PathName,
                    OldPath = oldSnap.SourcePath,
                    OldTitle = oldSnap.Title,
                    OldLabel = from.Label,
                    OldVersionGuid = oldSnap.VersionGuid,
                    OldFileTimeUtc = from.Kind == Source.SourceKind.File ? System.IO.File.GetLastWriteTimeUtc(from.Path) : oldSnap.TakenUtc,
                    NewPath = newSnap.SourcePath,
                    NewTitle = newSnap.Title,
                    NewLabel = to.Label,
                    NewVersionGuid = newSnap.VersionGuid,
                    OldSnapshotUtc = oldSnap.TakenUtc,
                    NewSnapshotUtc = newSnap.TakenUtc,
                    CreatedBy = uiapp.Application.Username,
                    CreatedUtc = DateTime.UtcNow,
                    OldCount = oldSnap.Elements.Count,
                    NewCount = newSnap.Elements.Count,
                    Added = changes.Count(c => c.Kind == ChangeKind.Added),
                    Deleted = changes.Count(c => c.Kind == ChangeKind.Deleted),
                    Modified = changes.Count(c => c.Kind == ChangeKind.Modified),
                    Tolerance = options.Tolerance,
                    Changes = changes,
                };
                rec.Matched = rec.NewCount - rec.Added;

                var result = new Result { Record = rec };
                if (rec.OldCount > 0 && (double)rec.Matched / rec.OldCount < MinMatchedShare)
                    result.Warning = $"Only {rec.Matched:N0} of {rec.OldCount:N0} elements in \"{from.Label}\" are in \"{to.Label}\". " +
                                     $"Are they really versions of the same model ({linkTitle})?";

                SyncStatusToast.Progress(title, 0.99, "Saving the results to the model…");
                ComparisonStore.Save(host, rec);
                SyncStatusToast.Show($"{linkTitle}: {rec.Added:N0} added, {rec.Deleted:N0} deleted, {rec.Modified:N0} modified.", 6000);
                return result;
            }
            catch
            {
                SyncStatusToast.Hide();
                throw;
            }
        }

        // One side: cached snapshot when there is one, else read it (and cache + record it for next time).
        private static LinkSnapshot SnapshotOf(UIApplication uiapp, Document linkDoc, string modelKey, Source s, string side,
                                               string title, double lo, double hi)
        {
            void Report(string text, double f) =>
                SyncStatusToast.Progress(title, f < 0 ? -1 : lo + (hi - lo) * Math.Max(0, Math.Min(1, f)), $"{side} · {StripStep(text)}");

            switch (s.Kind)
            {
                case Source.SourceKind.Cached:
                {
                    Report("Using the saved snapshot", 1);
                    var snap = SnapshotCache.TryLoadKey(s.Version.CacheKey);
                    if (snap == null) throw new InvalidOperationException($"The saved snapshot of \"{s.Label}\" is gone. Pick another version or the file.");
                    return snap;
                }
                case Source.SourceKind.File:
                {
                    // Same save already cached (e.g. downloaded twice, or seen as the live link)? Don't open it.
                    var dv = OldModelOpener.VersionOf(s.Path);
                    var known = dv == null ? null : VersionCatalog.FindByGuid(modelKey, dv.VersionGUID.ToString());
                    var hit = known == null ? null : SnapshotCache.TryLoadKey(known.CacheKey);
                    if (hit != null) { Report("Same version as a saved snapshot; not opening the file", 1); return hit; }

                    var snap = OldModelOpener.Snapshot(uiapp.Application, s.Path, Report, out string key);
                    if (key != null)
                        VersionCatalog.Register(modelKey, new LinkVersion
                        {
                            CacheKey = key, VersionGuid = snap.VersionGuid, Saves = snap.Saves, Title = snap.Title,
                            SourcePath = s.Path, SavedUtc = System.IO.File.GetLastWriteTimeUtc(s.Path),
                            SnapshotUtc = snap.TakenUtc, Elements = snap.Elements.Count,
                        });
                    return snap;
                }
                default:
                {
                    string guid = null; int saves = 0;
                    try
                    {
                        var dv = Document.GetDocumentVersion(linkDoc);
                        if (dv != null) { guid = dv.VersionGUID.ToString(); saves = dv.NumberOfSaves; }
                    }
                    catch { /* no version info */ }
                    var known = VersionCatalog.FindByGuid(modelKey, guid);
                    var hit = known == null ? null : SnapshotCache.TryLoadKey(known.CacheKey);
                    if (hit != null) { Report("Using the saved snapshot of the loaded version", 1); return hit; }

                    var readStart = DateTime.UtcNow;
                    var snap = SnapshotBuilder.Build(linkDoc, linkDoc.PathName, null,
                        (done, total) => Report($"Reading the loaded link: {done:N0} of {total:N0} elements{Eta(readStart, done, total)}",
                                                total == 0 ? 1 : (double)done / total));
                    snap.VersionGuid = guid;
                    snap.Saves = saves;
                    if (guid != null)
                    {
                        string key = SnapshotCache.VersionKey(guid);
                        if (SnapshotCache.SaveKey(snap, key))
                            VersionCatalog.Register(modelKey, new LinkVersion
                            {
                                CacheKey = key, VersionGuid = guid, Saves = saves, Title = linkDoc.Title, SourcePath = linkDoc.PathName,
                                FromLiveLink = true, SavedUtc = DateTime.UtcNow, SnapshotUtc = snap.TakenUtc, Elements = snap.Elements.Count,
                            });
                    }
                    return snap;
                }
            }
        }

        private static string StripStep(string text) => Regex.Replace(text ?? "", @"^Steps? [\d–-]+ of \d+ · ", "");

        /// <summary>" · about 2 min left" from the rate so far, or "" until there's enough to go on.</summary>
        internal static string Eta(DateTime start, int done, int total)
        {
            double secs = (DateTime.UtcNow - start).TotalSeconds;
            if (done < 200 || secs < 3 || total <= done) return "";
            double left = secs / done * (total - done);
            if (left < 50) return $" · about {Math.Max(5, Math.Round(left / 5) * 5):0} s left";
            return $" · about {Math.Ceiling(left / 60):0} min left";
        }

        // Which aspects / parameters drive the result: a comparison dominated by one parameter usually means noise
        // (a value Revit rewrites on every save) that belongs in DiffOptions.IgnoredParameters.
        private static void LogBreakdown(List<ElementChange> changes)
        {
            try
            {
                var aspects = Enum.GetValues(typeof(ChangeAspect)).Cast<ChangeAspect>().Where(a => a != ChangeAspect.None)
                    .Select(a => $"{a} {changes.Count(c => (c.Aspects & a) != 0):N0}");
                var onlyParams = changes.Count(c => c.Kind == ChangeKind.Modified && c.Aspects == ChangeAspect.Parameters);
                var top = changes.Where(c => c.ParameterChanges != null).SelectMany(c => c.ParameterChanges.Select(p => p.Name))
                    .GroupBy(n => n).OrderByDescending(g => g.Count()).Take(15).Select(g => $"{g.Key} ({g.Count():N0})");
                Log.Info($"[Federator] modified by aspect: {string.Join(", ", aspects)}; parameters-only: {onlyParams:N0}");
                Log.Info($"[Federator] most changed parameters: {string.Join(", ", top)}");
            }
            catch (Exception ex) { Log.Info($"[Federator] breakdown failed: {ex.Message}"); }
        }

        private static string SafeFullPath(string p)
        {
            try { return string.IsNullOrEmpty(p) ? "" : Path.GetFullPath(p); } catch { return p ?? ""; }
        }
    }
}
