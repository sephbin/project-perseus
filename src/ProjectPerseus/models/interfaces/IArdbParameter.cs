using System;

namespace ProjectPerseus.models.interfaces
{
    public interface IArdbParameter
    {
        String Guid { get; }
        IArdbDefinition Definition { get; }
        StorageType StorageType { get; }
        bool HasValue { get; }
        double AsDouble();
        IArdbElementId AsElementId();
        int AsInteger();
        string AsString();
    }
}