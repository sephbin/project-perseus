using System;
using ProjectPerseus.revit;
using ProjectPerseus.revit.interfaces;

namespace ProjectPerseusTests.mocks
{
    public class MockArdbParameter : IArdbParameter
    {
        public String Guid { get; set;  }
        public IArdbDefinition Definition { get; set; }
        public StorageType StorageType { get; set; }
        public bool HasValue { get; }
        public object Value { get; set; }
        
        public MockArdbParameter(string guid, MockArdbDefinition definition, StorageType storageType, object value)
        {
            Guid = guid;
            Definition = definition;
            StorageType = storageType;
            HasValue = value != null;
            Value = value;
        }

        
        public double AsDouble()
        {
            if (Value is double doubleValue)
            {
                return doubleValue;
            }
            throw new InvalidCastException("Value is not a double");
        }

        public IArdbElementId AsElementId()
        {
            if (Value is int intValue)
            {
                return new MockArdbElementId(intValue);
            }
            throw new InvalidCastException("Value is not an ElementId");
        }

        public int AsInteger()
        {
            if (Value is int intValue)
            {
                return intValue;
            }
            throw new InvalidCastException("Value is not an integer");
        }

        public string AsString()
        {
            if (Value is string stringValue)
            {
                return stringValue;
            }
            throw new InvalidCastException("Value is not a string");
        }
    }
}