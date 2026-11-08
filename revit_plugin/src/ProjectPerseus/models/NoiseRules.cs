using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>
    /// Per-link "this isn't a real change" rules, shared in the host model (federator/FederatorSettingsStore, key
    /// "noise:&lt;link instance uid&gt;"). A modification made up only of these parameters and / or a move shorter than
    /// MinMoveMm is noise: hidden by the panel's "Hide noise" filter, never deleted (federator/NoiseFilter).
    /// </summary>
    public class NoiseRules
    {
        /// <summary>Exact parameter names as in ElementChange.ParameterChanges (type parameters as "[Type] name").</summary>
        public List<string> Parameters { get; set; } = new List<string>();
        /// <summary>Moves shorter than this (mm) are noise; 0 = off.</summary>
        public double MinMoveMm { get; set; }
    }
}
