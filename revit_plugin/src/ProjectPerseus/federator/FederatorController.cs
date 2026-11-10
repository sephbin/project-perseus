using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ProjectPerseus.logging;
using ProjectPerseus.ui;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Revit events the federator panel follows while it's open: view switches (re-evaluate "Only in active view")
    /// and selection changes (Tab-selecting a linked element highlights its row). Subscribed once at startup like
    /// MarkerEngine; the handlers do nothing while ui/FederatorWindow is closed. They run in an API context.
    /// Also, always: every 20 s at idle, LinkArchive.Capture for each loaded link (archives versions not seen before).
    /// </summary>
    internal static class FederatorController
    {
        internal static void Subscribe(UIControlledApplication app)
        {
            app.ViewActivated += OnViewActivated;
            app.SelectionChanged += OnSelectionChanged;
            app.Idling += OnIdling;
        }

        private static DateTime _nextArchiveCheck = DateTime.MinValue;
        private static readonly TimeSpan ArchiveInterval = TimeSpan.FromSeconds(20);

        // Version archive: cheap per link when nothing changed (save GUID already known); copies run in the background.
        private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (DateTime.UtcNow < _nextArchiveCheck) return;
            _nextArchiveCheck = DateTime.UtcNow + ArchiveInterval;
            try
            {
                var uiapp = sender as UIApplication;
                if (uiapp == null) return;
                foreach (Document doc in uiapp.Application.Documents)
                {
                    if (doc.IsLinked || doc.IsFamilyDocument) continue;
                    foreach (var inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                        LinkArchive.Capture(uiapp.Application, doc, inst);
                }
            }
            catch (Exception ex) { Log.Info($"[Federator] archive check: {ex.Message}"); }
        }

        internal static void Unsubscribe(UIControlledApplication app)
        {
            app.ViewActivated -= OnViewActivated;
            app.SelectionChanged -= OnSelectionChanged;
            app.Idling -= OnIdling;
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try { FederatorWindow.Instance?.OnViewActivated(e.Document, e.CurrentActiveView); }
            catch (Exception ex) { Log.Info($"[Federator] view activated: {ex.Message}"); }
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var w = FederatorWindow.Instance;
                if (w != null) w.OnRevitSelection(e.GetDocument(), e.GetReferences());
            }
            catch (Exception ex) { Log.Info($"[Federator] selection changed: {ex.Message}"); }
        }
    }
}
