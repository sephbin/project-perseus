using System.Collections.Generic;

namespace ProjectPerseus.revit.interfaces
{
    public interface IArdbParameterSet
    {
        IEnumerator<IArdbParameter> GetEnumerator();
    }
}