using System;

namespace ProjectPerseus.models
{
    public class ActionDto
    {
        public string CorrelationId   { get; set; }  // client UUID, one per action
        public string SessionId       { get; set; }  // plugin session UUID
        public string DocGuid         { get; set; }  // Perseus document GUID
        public string ActionType      { get; set; }  // see ActionType constants
        public long?  ElementId       { get; set; }  // Revit integer element ID
        public string ElementUniqueId { get; set; }  // Revit UniqueId string
        public string ElementName     { get; set; }
        public string TransactionName { get; set; }  // raw Revit txn name (language-pack audit)
        public string RevitUser       { get; set; }
        public string TimestampUtc    { get; set; }  // ISO 8601
        public string Description     { get; set; }  // failure message text for warnings
    }

    public static class ActionType
    {
        public const string ElementDeleted   = "element_deleted";
        public const string Ungroup          = "ungroup";
        public const string LinkUnload       = "link_unload";
        public const string WarningDismissed = "warning_dismissed";
        public const string Unpin            = "unpin";
        public const string SheetViewEdit    = "sheet_view_edit";
    }
}
