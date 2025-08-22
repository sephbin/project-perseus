using ProjectPerseus.revit.interfaces;

namespace ProjectPerseusTests.mocks
{
    public class MockArdbDefinition : IArdbDefinition
    {
        public string Name { get; }
        public string ParameterGroup { get; }

        public MockArdbDefinition(string name, string parameterGroup)
        {
            Name = name;
            ParameterGroup = parameterGroup;
        }
    }
}