using System;
using System.Linq;
using System.Threading.Tasks;

namespace ProjectPerseus.logging
{
    /// <summary>
    /// Last-chance logging. An unhandled exception on a background thread (Thread body, Timer callback,
    /// async void) terminates Revit without a crash dump, from any add-in. These handlers can't stop that, but
    /// they leave a line in the Perseus log saying what died and whether Perseus code was on the stack, so a
    /// "Revit just vanished" report can be attributed. Unobserved Task exceptions are logged and marked observed.
    /// Installed first thing in Plugin.OnStartup.
    /// </summary>
    internal static class CrashTrace
    {
        private static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                TaskScheduler.UnobservedTaskException += OnUnobserved;
            }
            catch { /* logging must never break startup */ }
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                Log.Error($"[CrashTrace] UNHANDLED exception (terminating: {e.IsTerminating}; " +
                          $"Perseus on stack: {(IsOurs(ex) ? "yes" : "no")}): {ex}");
            }
            catch { }
        }

        private static void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                var ex = e.Exception?.Flatten();
                // Only ours: other add-ins' unobserved tasks are their business and would flood the log.
                if (!IsOurs(ex)) return;
                // Pollers stopping (Task.Delay cancelled on shutdown) are expected, not worth a warning.
                if (ex.InnerExceptions.All(x => x is OperationCanceledException)) { e.SetObserved(); return; }
                Log.Warn($"[CrashTrace] unobserved Task exception: {ex}");
                e.SetObserved();
            }
            catch { }
        }

        private static bool IsOurs(Exception ex) =>
            ex != null && (ex.ToString().IndexOf("ProjectPerseus", StringComparison.Ordinal) >= 0);
    }
}
