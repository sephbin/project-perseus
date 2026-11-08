using System;
using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Snapshot of an archived version of a consultant model that the user picked. Uses SnapshotCache when the
    /// file hasn't changed; otherwise copies it to a temp file with a unique name (Revit won't open a document
    /// whose title matches an open one - including the loaded link of the same model), marks the copy's links
    /// as not to be loaded (TransmissionData), opens it detached with all worksets, reads it and closes it
    /// without saving. Needs a Revit API context; blocks the UI thread for as long as the open + read takes.
    /// </summary>
    internal static class OldModelOpener
    {
        internal static LinkSnapshot Snapshot(Application app, string path, Action<string, double> progress)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The selected model file wasn't found.", path);

            var cached = SnapshotCache.TryLoad(path);
            if (cached != null)
            {
                Log.Info($"[Federator] using cached snapshot of {path}");
                return cached;
            }

            string stamp = SnapshotCache.StampOf(path);
            var info = BasicFileInfo.Extract(path);
            if (info.IsSavedInLaterVersion)
                throw new InvalidOperationException($"{Path.GetFileName(path)} was saved in a newer version of Revit than this one.");

            string tempDir = Path.Combine(Path.GetTempPath(), "ProjectPerseus", "federator");
            Directory.CreateDirectory(tempDir);
            string temp = Path.Combine(tempDir,
                Path.GetFileNameWithoutExtension(path) + "__perseus_base_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".rvt");

            Document doc = null;
            EventHandler<FailuresProcessingEventArgs> onFailures = (s, e) => DismissWarnings(e, temp);
            try
            {
                progress?.Invoke("Copying the old version…", -1);
                File.Copy(path, temp);
                ModelPath mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(temp);
                SkipLinks(mp);

                var options = new OpenOptions { Audit = false, AllowOpeningLocalByWrongUser = true };
                if (info.IsWorkshared)
                {
                    options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                }

                progress?.Invoke("Opening the old version…", -1);
                app.FailuresProcessing += onFailures;
                var started = DateTime.UtcNow;
                doc = app.OpenDocumentFile(mp, options);
                Log.Info($"[Federator] opened {path} (as {Path.GetFileName(temp)}) in {(DateTime.UtcNow - started).TotalSeconds:F1} s");

                var snap = SnapshotBuilder.Build(doc, path, stamp,
                    (done, total) => progress?.Invoke($"Reading the old version… {done:N0} / {total:N0}", total == 0 ? 1 : (double)done / total));
                snap.Title = Path.GetFileNameWithoutExtension(path); // not the temp copy's name
                SnapshotCache.Save(snap);
                return snap;
            }
            finally
            {
                app.FailuresProcessing -= onFailures;
                if (doc != null)
                {
                    try { doc.Close(false); }
                    catch (Exception ex) { Log.Warn($"[Federator] could not close the old version: {ex.Message}"); }
                }
                TryDelete(temp);
            }
        }

        /// <summary>Marks every Revit / CAD link of the copy as unloaded, so opening it doesn't load the old version's links.</summary>
        private static void SkipLinks(ModelPath mp)
        {
            try
            {
                var data = TransmissionData.ReadTransmissionData(mp);
                if (data == null) return;
                int n = 0;
                foreach (ElementId id in data.GetAllExternalFileReferenceIds())
                {
                    var r = data.GetLastSavedReferenceData(id);
                    if (r == null) continue;
                    var t = r.ExternalFileReferenceType;
                    if (t != ExternalFileReferenceType.RevitLink && t != ExternalFileReferenceType.CADLink) continue;
                    data.SetDesiredReferenceData(id, r.GetPath(), r.PathType, false);
                    n++;
                }
                if (n == 0) return;
                data.IsTransmitted = true;
                TransmissionData.WriteTransmissionData(mp, data);
                Log.Info($"[Federator] old version: {n} links set not to load");
            }
            catch (Exception ex)
            {
                Log.Info($"[Federator] couldn't unload the old version's links (they'll load, just slower): {ex.Message}");
            }
        }

        private static void DismissWarnings(FailuresProcessingEventArgs e, string tempPath)
        {
            try
            {
                var fa = e.GetFailuresAccessor();
                var doc = fa.GetDocument();
                if (doc == null || !string.Equals(doc.PathName, tempPath, StringComparison.OrdinalIgnoreCase)) return;
                fa.DeleteAllWarnings();
            }
            catch (Exception ex) { Log.Info($"[Federator] FailuresProcessing on old version: {ex.Message}"); }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                // A workshared detach can leave a "<name>_backup" folder beside the copy.
                string backup = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_backup");
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
            }
            catch (Exception ex) { Log.Info($"[Federator] temp copy left behind ({path}): {ex.Message}"); }
        }
    }
}
