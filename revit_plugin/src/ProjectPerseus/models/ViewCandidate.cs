namespace ProjectPerseus.models
{
    /// <summary>A view that can receive baked federator annotations (plans, sections, elevations, details).</summary>
    public class ViewCandidate
    {
        public string Uid { get; set; }
        public string Name { get; set; }
        public string ViewType { get; set; }
        /// <summary>Sheet number when placed on a sheet, else "".</summary>
        public string SheetNumber { get; set; } = "";
        public int Scale { get; set; }
    }
}
