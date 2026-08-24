namespace ProjectPerseus.models
{
    public class ViolationHighlightDto
    {
        public string ElementUniqueId { get; set; }
        public string RuleName        { get; set; }
        public string Severity        { get; set; }
        public string Message         { get; set; }
        public string ElementName     { get; set; }
    }
}
