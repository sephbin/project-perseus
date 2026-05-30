using System;
using Sentry;

namespace ProjectPerseus.logging
{
    // Scoped Sentry initialization for one Revit sync session. Construct at the top of a
    // sync handler inside `using (...)` — Sentry is initialized on enter, capture-ready
    // while the block runs, and shut down on dispose.
    //
    // Outside an active SentryContext, the Sentry SDK silently no-ops, so Log.Warn/Error/
    // Exception calls outside this scope still log to file but don't reach Sentry.
    public class SentryContext : IDisposable
    {
        public SentryContext(string pingId = null)
        {
            Init();
            SentrySdk.CaptureMessage(pingId ?? "Ping");
        }

        public void Dispose()
        {
            SentrySdk.Close();
        }

        public static void Ping(string pingId = null)
        {
            using (var sentry = new SentryContext(pingId)) { }
        }

        private static void Init()
        {
            SentrySdk.Init(options =>
            {
                // DSN is hardcoded for this project — see https://docs.sentry.io/product/sentry-basics/dsn-explainer/
                options.Dsn = "https://32e0a3644c9e180de912d15cae6df17c@o4506516579155968.ingest.sentry.io/4506516583546880";
                options.AutoSessionTracking = true;
                options.IsGlobalModeEnabled = true; // Revit is a client app, single global scope.
                options.EnableTracing = true;
            });
        }
    }
}
