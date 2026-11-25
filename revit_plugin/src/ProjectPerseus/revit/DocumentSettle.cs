using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace ProjectPerseus.revit
{
    /// <summary>
    /// Tells idle-time readers (ElementCategoryCache, IdIndexMaintainer, OwnershipTracker) when a document is safe
    /// to read element by element. A document is busy from Saving / SavingAs / SynchronizingWithCentral /
    /// ReloadingLatest until the matching completed event, then settles for <see cref="SettleSeconds"/>. Element.Name
    /// on a 380k-element cloud model crashed Revit (native AV, uncatchable) on the first idle ticks right after a save
    /// (2026-11-25, Data Exchange Connector's API save). <see cref="Epoch"/> counts completed saves / syncs / reloads,
    /// so a job holding an ElementId list taken earlier knows to take a fresh one.
    /// Main thread only (Revit events + Idling).
    /// </summary>
    internal static class DocumentSettle
    {
        internal const int SettleSeconds = 5;

        private class State
        {
            public int Busy;                       // nested operations in progress
            public DateTime SettledAt = DateTime.MinValue;
            public int Epoch;
        }

        private static readonly Dictionary<Document, State> _docs = new Dictionary<Document, State>();

        internal static void Subscribe(UIControlledApplication app)
        {
            var c = app.ControlledApplication;
            c.DocumentSaving += OnSaving;
            c.DocumentSaved += OnSaved;
            c.DocumentSavingAs += OnSavingAs;
            c.DocumentSavedAs += OnSavedAs;
            c.DocumentSynchronizingWithCentral += OnSyncing;
            c.DocumentSynchronizedWithCentral += OnSynced;
            c.DocumentReloadingLatest += OnReloading;
            c.DocumentReloadedLatest += OnReloaded;
            c.DocumentClosing += OnClosing;
        }

        internal static void Unsubscribe(UIControlledApplication app)
        {
            var c = app.ControlledApplication;
            c.DocumentSaving -= OnSaving;
            c.DocumentSaved -= OnSaved;
            c.DocumentSavingAs -= OnSavingAs;
            c.DocumentSavedAs -= OnSavedAs;
            c.DocumentSynchronizingWithCentral -= OnSyncing;
            c.DocumentSynchronizedWithCentral -= OnSynced;
            c.DocumentReloadingLatest -= OnReloading;
            c.DocumentReloadedLatest -= OnReloaded;
            c.DocumentClosing -= OnClosing;
            _docs.Clear();
        }

        /// <summary>False while the document is being saved / synced / reloaded and for a few seconds after.</summary>
        internal static bool IsSettled(Document doc)
        {
            if (doc == null || !_docs.TryGetValue(doc, out var st)) return true;
            return st.Busy == 0 && DateTime.UtcNow >= st.SettledAt;
        }

        /// <summary>Number of saves / syncs / reloads completed on this document (this session).</summary>
        internal static int Epoch(Document doc) =>
            doc != null && _docs.TryGetValue(doc, out var st) ? st.Epoch : 0;

        private static State For(Document doc)
        {
            if (!_docs.TryGetValue(doc, out var st)) _docs[doc] = st = new State();
            return st;
        }

        private static void Begin(Document doc)
        {
            if (doc == null) return;
            For(doc).Busy++;
        }

        private static void End(Document doc)
        {
            if (doc == null) return;
            var st = For(doc);
            if (st.Busy > 0) st.Busy--;
            st.Epoch++;
            st.SettledAt = DateTime.UtcNow.AddSeconds(SettleSeconds);
        }

        private static void OnSaving(object s, DocumentSavingEventArgs e) => Begin(e.Document);
        private static void OnSaved(object s, DocumentSavedEventArgs e) => End(e.Document);
        private static void OnSavingAs(object s, DocumentSavingAsEventArgs e) => Begin(e.Document);
        private static void OnSavedAs(object s, DocumentSavedAsEventArgs e) => End(e.Document);
        private static void OnSyncing(object s, DocumentSynchronizingWithCentralEventArgs e) => Begin(e.Document);
        private static void OnSynced(object s, DocumentSynchronizedWithCentralEventArgs e) => End(e.Document);
        private static void OnReloading(object s, DocumentReloadingLatestEventArgs e) => Begin(e.Document);
        private static void OnReloaded(object s, DocumentReloadedLatestEventArgs e) => End(e.Document);

        private static void OnClosing(object s, DocumentClosingEventArgs e)
        {
            if (e.Document != null) _docs.Remove(e.Document);
        }
    }
}
