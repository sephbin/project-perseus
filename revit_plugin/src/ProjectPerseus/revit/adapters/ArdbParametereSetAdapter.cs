using System.Collections.Generic;
using ProjectPerseus.revit.interfaces;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.revit.adapters
{
    public class ArdbParameterSetAdapter : IArdbParameterSet
    {
        private readonly ARDB.ParameterSet _parameterSet;

        public ArdbParameterSetAdapter(ARDB.ParameterSet parameterSet)
        {
            _parameterSet = parameterSet;
        }

        public IEnumerator<IArdbParameter> GetEnumerator()
        {
            foreach (ARDB.Parameter parameter in _parameterSet)
            {
                yield return new ArdbParameterAdapter(parameter);
            }
        }
    }
}