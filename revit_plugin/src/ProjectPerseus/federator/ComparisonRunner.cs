using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.ui;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// One comparison end to end: snapshot the archived file (OldModelOpener) and the loaded link (SnapshotBuilder),
    /// diff them (LinkDiff) and save the result in the host model (ComparisonStore). Progress goes to
    /// ui/SyncStatusToast, which paints on its own thread while this blocks the UI thread. Needs a Revit API
    /// context with no open transaction (a command or an ExternalEvent).
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

        internal static Result Run(UIApplication uiapp, Document host, RevitLinkInstance instance, string oldPath,
                                   DiffOptions options = null)
        {
            options = options ?? new DiffOptions();
            Document linkDoc = instance.GetLinkDocument()
                ?? throw new InvalidOperationException("The link isn't loaded. Reload it, then compare.");
            if (string.Equals(Path.GetFullPath(oldPath), SafeFullPath(linkDoc.PathName), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That's the file the link is loaded from. Pick an archived earlier version.");

            try
            {
                string title = "Comparing " + linkDoc.Title;
                // Bar: old version 0–55 % (unknown while copying / opening), current link 55–95 %, compare + save the rest.
                var oldSnap = OldModelOpener.Snapshot(uiapp.Application, oldPath,
                    (text, f) => SyncStatusToast.Progress(title, f < 0 ? -1 : 0.15 + 0.40 * f, text));

                var readStart = DateTime.UtcNow;
                var newSnap = SnapshotBuilder.Build(linkDoc, linkDoc.PathName, null,
                    (done, total) => SyncStatusToast.Progress(title, 0.55 + 0.40 * (total == 0 ? 1 : (double)done / total),
                        $"Step 4 of 5 · Reading the current link: {done:N0} of {total:N0} elements{Eta(readStart, done, total)}"));

                SyncStatusToast.Progress(title, 0.96, "Step 5 of 5 · Comparing and finding likely replacements…");
                var changes = LinkDiff.Compare(oldSnap, newSnap, options);
                ReplacementMatcher.Suggest(changes);
                LogBreakdown(changes);

                var rec = new ComparisonRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    LinkInstanceUid = instance.UniqueId,
                    LinkTitle = linkDoc.Title,
                    LinkPath = linkDoc.PathName,
                    OldPath = oldPath,
                    OldTitle = oldSnap.Title,
                    OldFileTimeUtc = File.GetLastWriteTimeUtc(oldPath),
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
                    result.Warning = $"Only {rec.Matched:N0} of {rec.OldCount:N0} elements in {oldSnap.Title} are in the " +
                                     $"current link. Is it really an earlier version of {linkDoc.Title}?";

                SyncStatusToast.Progress(title, 0.99, "Step 5 of 5 · Saving the results to the model…");
                ComparisonStore.Save(host, rec);
                SyncStatusToast.Show($"{linkDoc.Title}: {rec.Added:N0} added, {rec.Deleted:N0} deleted, {rec.Modified:N0} modified.", 6000);
                return result;
            }
            catch
            {
                SyncStatusToast.Hide();
                throw;
            }
        }

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
