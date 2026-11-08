using System;
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
    /// </summary>
    internal static class FederatorController
    {
        internal static void Subscribe(UIControlledApplication app)
        {
            app.ViewActivated += OnViewActivated;
            app.SelectionChanged += OnSelectionChanged;
        }

        internal static void Unsubscribe(UIControlledApplication app)
        {
            app.ViewActivated -= OnViewActivated;
            app.SelectionChanged -= OnSelectionChanged;
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
