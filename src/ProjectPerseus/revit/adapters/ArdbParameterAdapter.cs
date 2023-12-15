using ProjectPerseus.models.interfaces;
using ProjectPerseus.revit.adapters;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.models.adapters
{
    public class ArdbParameterAdapter : IArdbParameter
    {
        private readonly ARDB.Parameter _parameter;

        public ArdbParameterAdapter(ARDB.Parameter parameter)
        {
            _parameter = parameter;
        }

        public IArdbDefinition Definition => new ArdbDefinitionAdapter(_parameter.Definition);
        public StorageType StorageType => (StorageType)_parameter.StorageType;
        public double AsDouble() => _parameter.AsDouble();
        public IArdbElementId AsElementId() => new ArdbElementIdAdapter(_parameter.AsElementId());
        public int AsInteger() => _parameter.AsInteger();
        public string AsString() => _parameter.AsString();
    }
}