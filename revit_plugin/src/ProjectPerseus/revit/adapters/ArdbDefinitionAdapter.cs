using ProjectPerseus.revit.interfaces;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.revit.adapters
{
    public class ArdbDefinitionAdapter : IArdbDefinition
    {
        private readonly ARDB.Definition _definition;

        public ArdbDefinitionAdapter(ARDB.Definition definition)
        {
            _definition = definition;
        }

        public string Name => _definition.Name;
        public string ParameterGroup
        {
            get
            {
#if REVIT2024_OR_GREATER
                // 2024+ uses the new ForgeTypeId system
                return _definition.GetGroupTypeId().TypeId;
#else
            // 2023 and older use the classic enum
            return _definition.ParameterGroup.ToString();
#endif
            }
        }
    }
}