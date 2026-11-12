namespace ProjectPerseus.mapping
{
    /// <summary>
    /// What triage says about one consultant type (from the federator's review of the current comparison), used to
    /// suggest which types to map: types whose changes people reviewed (accepted / queried / resolved) or left open are
    /// worth mapping; types whose changes were all ignored or are noise (or whose category is a noise category) aren't.
    /// Pure: unit-tested in ProjectPerseusTests/MappingMathTest.cs.
    /// </summary>
    public class TypeTriage
    {
        public const string Suggested = "Suggested", NotWanted = "Not wanted", Unchanged = "No changes";

        public int Changes { get; set; }
        public int Cared { get; set; }      // Accepted / Query / Resolved
        public int Ignored { get; set; }
        public int Noise { get; set; }
        public bool NoiseCategory { get; set; }

        public string Verdict
        {
            get
            {
                if (NoiseCategory) return NotWanted;
                if (Cared > 0) return Suggested;
                if (Changes == 0) return Unchanged;
                return Ignored + Noise >= Changes ? NotWanted : Suggested;
            }
        }

        public string Describe()
        {
            if (NoiseCategory) return "noise category";
            if (Changes == 0) return "no changes in this comparison";
            var parts = $"{Changes} change(s)";
            if (Cared > 0) parts += $", {Cared} reviewed";
            if (Ignored > 0) parts += $", {Ignored} ignored";
            if (Noise > 0) parts += $", {Noise} noise";
            return parts;
        }
    }
}
