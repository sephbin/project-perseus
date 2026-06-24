namespace ProjectPerseus.models
{
    public enum FilterAction { Exclude, Include }

    public class KeyScheduleFilter
    {
        public FilterAction Action     { get; set; }
        public string       Expression { get; set; }
    }
}
