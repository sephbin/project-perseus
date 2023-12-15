namespace ProjectPerseus.models.interfaces
{
    public interface IArdbParameter
    {
        IArdbDefinition Definition { get; }
        StorageType StorageType { get; }
        double AsDouble();
        IArdbElementId AsElementId();
        int AsInteger();
        string AsString();
    }
}