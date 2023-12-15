using System.Collections.Generic;

namespace ProjectPerseus.models.interfaces
{
    public interface IArdbParameterSet
    {
        IEnumerator<IArdbParameter> GetEnumerator();
    }
}