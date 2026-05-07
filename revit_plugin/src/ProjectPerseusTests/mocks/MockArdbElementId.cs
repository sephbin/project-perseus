using ProjectPerseus.revit.interfaces;

namespace ProjectPerseusTests.mocks
{
    public class MockArdbElementId : IArdbElementId
    {

        public long Value { get; }


        public MockArdbElementId(long id)
        {
            Value = id;
        }
    }
}