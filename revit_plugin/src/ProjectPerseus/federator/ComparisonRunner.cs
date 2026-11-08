using System;
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
                var oldSnap = OldModelOpener.Snapshot(uiapp.Application, oldPath,
                    (text, f) => SyncStatusToast.Progress("Comparing " + linkDoc.Title, f < 0 ? -1 : f * 0.6, text));

                var newSnap = SnapshotBuilder.Build(linkDoc, linkDoc.PathName, null,
                    (done, total) => SyncStatusToast.Progress("Comparing " + linkDoc.Title,
                        0.6 + 0.35 * (total == 0 ? 1 : (double)done / total), $"Reading the current link… {done:N0} / {total:N0}"));

                SyncStatusToast.Progress("Comparing " + linkDoc.Title, 0.96, "Comparing…");
                var changes = LinkDiff.Compare(oldSnap, newSnap, options);
                ReplacementMatcher.Suggest(changes);

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

                SyncStatusToast.Progress("Comparing " + linkDoc.Title, 0.99, "Saving the results to the model…");
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

        private static string SafeFullPath(string p)
        {
            try { return string.IsNullOrEmpty(p) ? "" : Path.GetFullPath(p); } catch { return p ?? ""; }
        }
    }
}
