using System.Collections.Generic;

namespace ProjectPerseus.models
{
    public class KeyScheduleData
    {
        public string ScheduleName { get; set; }
        public List<string> ColumnNames { get; set; }
        public List<Dictionary<string, string>> Rows { get; set; }

        public KeyScheduleData()
        {
            ColumnNames = new List<string>();
            Rows = new List<Dictionary<string, string>>();
        }
    }
}
