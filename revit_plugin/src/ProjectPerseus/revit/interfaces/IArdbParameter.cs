namespace ProjectPerseus.revit.interfaces
{
    public interface IArdbParameter
    {
        string Guid { get; }
        long? DefinitionId { get; }
        IArdbDefinition Definition { get; }
        StorageType StorageType { get; }
        bool HasValue { get; }
        double AsDouble();
        IArdbElementId AsElementId();
        int AsInteger();
        string AsString();
        string GetSpecType();
    }
}