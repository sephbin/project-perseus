using System;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ProjectPerseus.logging;
using ProjectPerseus.queue;
using ProjectPerseus.ui;

namespace ProjectPerseus.ownership
{
    /// <summary>
    /// Keeps the status-bar ownership badge current for the active document. Edits, syncs, document switches and
    /// relinquishing mark the count dirty; the recount happens on Idling, debounced, and the minimum gap between
    /// recounts grows with how long a recount takes so big models stay responsive. A slow periodic recount also
    /// catches changes no event reports (e.g. Revit's own Relinquish All Mine).
    /// </summary>
    internal static class OwnershipTracker
    {
        private static readonly TimeSpan Periodic = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MinGapFloor = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MinGapCeiling = TimeSpan.FromSeconds(60);

        private static volatile bool _dirty = true;
        private static DateTime _lastScan = DateTime.MinValue;
        private static TimeSpan _minGap = MinGapFloor;
        private static string _lastDocKey;
        private static bool _refreshFormAfterSync;

        internal static void Subscribe(UIControlledApplication app)
        {
            OwnershipUiEvent.Create();
            OwnershipBadgeController.OnClick = () => OwnershipUiEvent.Run(OwnershipForm.Open);

            app.Idling += OnIdling;
            app.ViewActivated += OnViewActivated;
            app.ControlledApplication.DocumentChanged += OnDocumentChanged;
            app.ControlledApplication.DocumentSynchronizedWithCentral += OnSynced;
            app.ControlledApplication.DocumentClosed += OnDocumentClosed;
        }

        internal static void Unsubscribe(UIControlledApplication app)
        {
            app.Idling -= OnIdling;
            app.ViewActivated -= OnViewActivated;
            app.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            app.ControlledApplication.DocumentSynchronizedWithCentral -= OnSynced;
            app.ControlledApplication.DocumentClosed -= OnDocumentClosed;
            OwnershipBadgeController.Shutdown();
        }

        internal static void MarkDirty() => _dirty = true;

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e) => _dirty = true;
        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e) => _dirty = true;
        private static void OnDocumentClosed(object sender, DocumentClosedEventArgs e) => _dirty = true;

        private static void OnSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            _dirty = true;
            _lastScan = DateTime.MinValue; // show the post-sync count straight away
            _refreshFormAfterSync = true;
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication app)) return;
            try
            {
                OwnershipBadgeController.Initialize(app.MainWindowHandle);

                if (_refreshFormAfterSync)
                {
                    _refreshFormAfterSync = false;
                    OwnershipForm.RefreshIfOpen(app);
                }

                Document doc = app.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsWorkshared || doc.IsFamilyDocument)
                {
                    OwnershipBadgeController.Hide();
                    _lastDocKey = null;
                    return;
                }

                string key = doc.PathName + "|" + doc.Title;
                DateTime now = DateTime.UtcNow;
                bool switched = key != _lastDocKey;
                bool due = _dirty || switched || now - _lastScan >= Periodic;
                if (!due || (!switched && now - _lastScan < _minGap)) return;

                var clock = Stopwatch.StartNew();
                int count = OwnershipScanner.CountMine(doc, out int worksets);
                long ms = clock.ElapsedMilliseconds;

                _dirty = false;
                _lastScan = now;
                _lastDocKey = key;
                // A recount that takes 300 ms waits at least 3 s before the next one.
                _minGap = TimeSpan.FromMilliseconds(Math.Min(MinGapCeiling.TotalMilliseconds,
                                                             Math.Max(MinGapFloor.TotalMilliseconds, ms * 10)));
                if (ms > 500) Log.Info($"[Ownership] badge recount took {ms} ms; next recount no sooner than {_minGap.TotalSeconds:0} s");

                OwnershipBadgeController.Show(count, worksets, doc.Title);
            }
            catch (Exception ex)
            {
                _dirty = false;
                _lastScan = DateTime.UtcNow;
                Log.Warn($"[Ownership] badge update failed: {ex.Message}");
            }
        }
    }
}
