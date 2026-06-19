using System.Collections.Generic;

namespace ProjectPerseus.models
{
    public class KeyScheduleImportPlan
    {
        public string ScheduleName { get; set; }
        public List<string> ColumnNames { get; set; }
        public List<Dictionary<string, string>> RowsToCreate { get; set; }
        public List<Dictionary<string, string>> RowsToUpdate { get; set; }
        public List<string> KeysToDelete { get; set; }
        public bool ScheduleNotFound { get; set; }
        public int OrphanRowCount { get; set; }  // rows with empty key name (failed prior imports)

        public KeyScheduleImportPlan()
        {
            ColumnNames = new List<string>();
            RowsToCreate = new List<Dictionary<string, string>>();
            RowsToUpdate = new List<Dictionary<string, string>>();
            KeysToDelete = new List<string>();
        }
    }
}
