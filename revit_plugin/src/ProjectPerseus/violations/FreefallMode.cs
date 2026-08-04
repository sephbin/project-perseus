namespace ProjectPerseus.violations
{
    // Session-level bypass for all violation enforcement. Set by FreefallCommand;
    // automatically cleared in SyncOrchestrator.doOnSync after each sync completes.
    internal static class FreefallMode
    {
        internal static bool IsActive { get; set; } = false;
    }
}
