using System;
using ProjectPerseus.models.interfaces;
using ARDB = Autodesk.Revit.DB;
using StorageType = ProjectPerseus.models.StorageType;

namespace ProjectPerseus.revit.adapters
{
    public class ArdbParameterAdapter : IArdbParameter
    {
        private readonly ARDB.Parameter _parameter;

        public ArdbParameterAdapter(ARDB.Parameter parameter)
        {
            _parameter = parameter ?? throw new ArgumentNullException(nameof(parameter));
        }
        
        public String Guid => _parameter.GUID.ToString();
        public IArdbDefinition Definition => _parameter.Definition == null ? null : new ArdbDefinitionAdapter(_parameter.Definition);
        public StorageType StorageType
        {
            get
            {
                try {
                    return (StorageType)_parameter.StorageType;
                } catch (AccessViolationException) {
                    // catch the AccessViolationException thrown by Revit when trying to access the StorageType of a parameter
                    return StorageType.Null;
                }
            }
        }

        public bool HasValue => _parameter.HasValue;
        public double AsDouble() => _parameter.AsDouble();
        public IArdbElementId AsElementId() => new ArdbElementIdAdapter(_parameter.AsElementId());
        public int AsInteger() => _parameter.AsInteger();
        public string AsString() => _parameter.AsString();
    }
}