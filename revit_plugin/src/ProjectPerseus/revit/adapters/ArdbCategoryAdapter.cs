using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.revit.interfaces;

namespace ProjectPerseus.revit.adapters
{
    public class ArdbCategoryAdapter : IArdbElement
    {
        private readonly Category _category;
        private readonly string _docGuid;

        public ArdbCategoryAdapter(Category category, string docGuid)
        {
            _category = category;
            _docGuid = docGuid;
        }

        public IArdbElementId Id => new ArdbElementIdAdapter(_category.Id);

        // Scoped to the document so two sources never share the same DB row for the same category.
        public string UniqueId => $"CATEGORY-{_docGuid}-{_category.Id.GetIdValue()}";

        public string Name => _category.Name;

        // Categories don't have a "Category", so we return null or itself
        public Category CategoryName => _category;

        // Categories don't have standard Parameters. We return an empty list to prevent crashes.
        public IArdbParameterSet ParametersSet => new EmptyParameterSet();

        // The empty parameter set helper
        private class EmptyParameterSet : IArdbParameterSet, System.Collections.IEnumerable
        {
            // Implementation for the generic enumerator (modern)
            public IEnumerator<IArdbParameter> GetEnumerator()
            {
                yield break;
            }

            // Implementation for the legacy enumerator (required by IEnumerable)
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                yield break;
            }
        }
    }
}