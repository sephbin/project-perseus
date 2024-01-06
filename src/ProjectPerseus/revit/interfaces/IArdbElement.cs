namespace ProjectPerseus.revit.interfaces
{
    public interface IArdbElement
    {
        IArdbElementId Id { get; }
        string UniqueId { get; }
        string Name { get; }
        IArdbParameterSet ParametersSet { get; }
    }
}