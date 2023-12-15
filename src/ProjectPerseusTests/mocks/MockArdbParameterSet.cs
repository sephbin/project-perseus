using System.Collections.Generic;
using ProjectPerseus.models.interfaces;

namespace ProjectPerseusTests.mocks
{
    public class MockArdbParameterSet : IArdbParameterSet
    {
        private readonly List<IArdbParameter> _parameters;

        public MockArdbParameterSet(List<IArdbParameter> parameters)
        {
            _parameters = parameters;
        }

        public IEnumerator<IArdbParameter> GetEnumerator()
        {
            return _parameters.GetEnumerator();
        }
    }
}