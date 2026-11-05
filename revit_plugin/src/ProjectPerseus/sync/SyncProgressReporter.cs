using ProjectPerseus.ui;

namespace ProjectPerseus.sync
{
    /// <summary>
    /// Wording for the post-sync upload progress shown on ui/SyncStatusToast: finding changes, collecting element data
    /// (per element), uploading (per chunk) and finishing. The runners call these; the toast throttles the updates.
    /// </summary>
    internal static class SyncProgressReporter
    {
        private const string Title = "Sending model changes to Perseus";

        internal static void Stage(string text) => SyncStatusToast.Progress(Title + "\n" + text, -1);

        // Matches Action<int, int> so it can be passed straight to ElementPayload.Build.
        internal static void Collecting(int done, int total)
        {
            if (total <= 0) return;
            SyncStatusToast.Progress($"{Title}\nCollecting {total:N0} element(s)…", (double)done / total,
                                     $"{done:N0} of {total:N0} · Revit is busy until this finishes");
        }

        internal static void Uploading(int sentChunks, int totalChunks, int elements)
        {
            string text = $"{Title}\nUploading {elements:N0} element(s)…";
            if (totalChunks <= 1 || sentChunks == 0)
                SyncStatusToast.Progress(text, -1, totalChunks > 1 ? $"part 1 of {totalChunks}" : null);
            else
                SyncStatusToast.Progress(text, (double)sentChunks / totalChunks, $"part {System.Math.Min(sentChunks + 1, totalChunks)} of {totalChunks}");
        }

        internal static void Done(string text) => SyncStatusToast.Show(text, 3000);

        internal static void Failed(string text) => SyncStatusToast.Show(text, 10000);
    }
}
