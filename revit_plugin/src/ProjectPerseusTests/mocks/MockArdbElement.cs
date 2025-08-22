using ProjectPerseus.revit.interfaces;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseusTests.mocks
{
    public class MockArdbElement : IArdbElement
    {
        public IArdbElementId Id { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public IArdbParameterSet ParametersSet { get; set; }
        public ARDB.Category CategoryName { get; set; }

        public MockArdbElement(
            IArdbElementId id, 
            string uniqueId, 
            string name, 
            IArdbParameterSet parametersMap,
            ARDB.Category categoryName
            )
        {
            Id = id;
            UniqueId = uniqueId;
            Name = name;
            ParametersSet = parametersMap;
            CategoryName = categoryName;
        }
    }
}