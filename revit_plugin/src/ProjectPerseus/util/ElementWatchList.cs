using System.Collections.Generic;

namespace ProjectPerseus.util
{
    public static class ElementWatchList
    {
        public static HashSet<long> Ids { get; } = new HashSet<long>();

        public static void Set(IEnumerable<long> ids)
        {
            Ids.Clear();
            foreach (var id in ids) Ids.Add(id);
        }
    }
}
