using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>
    /// The host views the federator uses to show changes (federator/ReviewViews), in priority order, shared in the model
    /// (FederatorSettingsStore "review-views"). Changes not visible in the active view open the first review view that
    /// shows them (before the federator 3D view); the panel shows changes on captured images of these views.
    /// </summary>
    public class ReviewViewSettings
    {
        public List<string> ViewUids { get; set; } = new List<string>();
        /// <summary>Capture images of the review views when a comparison opens (missing / out of date only).</summary>
        public bool AutoCapture { get; set; } = true;
        /// <summary>Longest side of a captured image, pixels.</summary>
        public int MaxPixels { get; set; } = 6000;
    }
}
