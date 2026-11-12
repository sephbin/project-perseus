using Autodesk.Revit.DB;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// The family mapping of a consultant model (models/FamilyMapping), shared in the host model through
    /// federator/FederatorSettingsStore (append-only, newest wins) under "familymap:&lt;consultant model key&gt;", so it
    /// follows the consultant model (federator/VersionCatalog.ModelKey) rather than one link instance. API context.
    /// </summary>
    internal static class FamilyMapStore
    {
        private static string Key(string modelKey) => "familymap:" + modelKey;

        internal static FamilyMapping Load(Document doc, string modelKey) =>
            FederatorSettingsStore.Load<FamilyMapping>(doc, Key(modelKey)) ?? new FamilyMapping();

        internal static void Save(Document doc, string modelKey, FamilyMapping mapping, string user) =>
            FederatorSettingsStore.Save(doc, Key(modelKey), mapping, user);
    }
}
