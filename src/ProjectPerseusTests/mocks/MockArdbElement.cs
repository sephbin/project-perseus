using ProjectPerseus.models.interfaces;

namespace ProjectPerseusTests.mocks
{
    public class MockArdbElement : IArdbElement
    {
        public IArdbElementId Id { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public IArdbParameterSet ParametersSet { get; set; }

        public MockArdbElement(IArdbElementId id, string uniqueId, string name, IArdbParameterSet parametersMap)
        {
            Id = id;
            UniqueId = uniqueId;
            Name = name;
            ParametersSet = parametersMap;
        }
    }
}