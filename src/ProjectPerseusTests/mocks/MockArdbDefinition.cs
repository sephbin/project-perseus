using ProjectPerseus.models.interfaces;

namespace ProjectPerseusTests.mocks
{
    public class MockArdbDefinition : IArdbDefinition
    {
        public string Name { get; }

        public MockArdbDefinition(string name)
        {
            Name = name;
        }
    }
}