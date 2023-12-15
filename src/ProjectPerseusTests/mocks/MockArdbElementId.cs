using ProjectPerseus.models.interfaces;

namespace ProjectPerseusTests.mocks
{
    public class MockArdbElementId : IArdbElementId
    {
        public int IntegerValue { get; }
        
        public MockArdbElementId(int integerValue)
        {
            IntegerValue = integerValue;
        }
    }
}