namespace ProjectPerseus.models.interfaces
{
    public interface IArdbElement
    {
        IArdbElementId Id { get; }
        string UniqueId { get; }
        string Name { get; }
        IArdbParameterSet ParametersSet { get; }
    }
}