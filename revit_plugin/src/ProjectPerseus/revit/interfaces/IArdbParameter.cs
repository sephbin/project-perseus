namespace ProjectPerseus.revit.interfaces
{
    public interface IArdbParameter
    {
        IArdbDefinition Definition { get; }
        StorageType StorageType { get; }
        bool HasValue { get; }
        double AsDouble();
        IArdbElementId AsElementId();
        int AsInteger();
        string AsString();
    }
}