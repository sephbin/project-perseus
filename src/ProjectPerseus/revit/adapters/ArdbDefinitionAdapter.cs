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
    }
}