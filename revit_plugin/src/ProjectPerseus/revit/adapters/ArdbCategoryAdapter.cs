using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.revit.interfaces;

namespace ProjectPerseus.revit.adapters
{
    public class ArdbCategoryAdapter : IArdbElement
    {
        private readonly Category _category;

        public ArdbCategoryAdapter(Category category)
        {
            _category = category;
        }

        public IArdbElementId Id => new ArdbElementIdAdapter(_category.Id);

        // Categories don't have a GUID, so we make a stable one using the Integer ID
        public string UniqueId => $"CATEGORY-{_category.Id.IntegerValue}";

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