namespace ProjectPerseus.models
{
    public enum StorageType
    {
        None,
        Integer,
        Double,
        String,
        ElementId,
        Null // means we couldn't access the Revit parameter StorageType
    }
}