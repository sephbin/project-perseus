using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using ProjectPerseus.logging;

namespace ProjectPerseus.sync
{
    /// <summary>
    /// Plain copy of the choices the user made in Revit's Synchronize with Central dialog (comment, compact,
    /// relinquish flags). Taken in the pre-sync hook, because the event's own options object is only valid during
    /// the event. When Perseus cancels a sync to write IDs, its re-sync uses these so the user's sync behaves
    /// exactly as they asked, and anything Perseus borrowed is relinquished whenever they relinquish borrowed elements.
    /// </summary>
    public class UserSyncOptions
    {
        // Defaults match Revit's dialog defaults (everything relinquished) for when no event was captured.
        public string Comment = "";
        public bool Compact;
        public bool RelinquishBorrowedElements = true;
        public bool RelinquishUserWorksets = true;
        public bool RelinquishStandardWorksets = true;
        public bool RelinquishViewWorksets = true;
        public bool RelinquishFamilyWorksets = true;

        public static UserSyncOptions From(DocumentSynchronizingWithCentralEventArgs e)
        {
            var o = new UserSyncOptions();
            try
            {
                o.Comment = e.Comments ?? "";
                var opts = e.Options;
                if (opts != null)
                {
                    o.Compact = opts.Compact;
                    o.RelinquishBorrowedElements = opts.RelinquishBorrowedElements;
                    o.RelinquishUserWorksets = opts.RelinquishUserCreatedWorksets;
                    o.RelinquishStandardWorksets = opts.RelinquishProjectStandardWorksets;
                    o.RelinquishViewWorksets = opts.RelinquishViewWorksets;
                    o.RelinquishFamilyWorksets = opts.RelinquishFamilyWorksets;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[UserSyncOptions] Could not read the user's sync options; using Revit defaults: {ex.Message}");
            }
            return o;
        }

        public SynchronizeWithCentralOptions Build()
        {
            var opts = new SynchronizeWithCentralOptions
            {
                Comment = string.IsNullOrWhiteSpace(Comment) ? "Perseus: IDs updated before sync" : Comment,
                Compact = Compact,
                SaveLocalAfter = true,
            };
            opts.SetRelinquishOptions(new RelinquishOptions(false)
            {
                CheckedOutElements = RelinquishBorrowedElements,
                UserWorksets = RelinquishUserWorksets,
                StandardWorksets = RelinquishStandardWorksets,
                ViewWorksets = RelinquishViewWorksets,
                FamilyWorksets = RelinquishFamilyWorksets,
            });
            return opts;
        }
    }
}
