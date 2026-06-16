using System;
using System.IO;
using Sentry;
using ProjectPerseus.config;

namespace ProjectPerseus.logging
{
    // Unified logging front door. P7 (2026-05-30) consolidated Utl.WriteLog (file-only)
    // and the old root Log (Sentry+console+file via Utl) into this single class.
    //
    // Severity routing — file output is the primary channel since it's the only reliable
    // way to get diagnostic data out of Revit:
    //   Info       → file only (most common; high-volume diagnostic trace)
    //   Warn       → file + Sentry warning + console
    //   Error      → file + Sentry error + console
    //   Exception  → file + Sentry exception capture + console
    //
    // File path: %AppData%\ProjectPerseus\logs\<timestamp>-<user>-medusa.log (per session,
    // created by InitSession). Falls back to %AppData%\ProjectPerseus\medusa.log if a call
    // arrives before InitSession (e.g. a static initializer logging something).
    public static class Log
    {
        private static string _sessionLogPath;
        private static readonly object _logLock = new object();
        private static string _lastWritten;   // dedup key of last line written to file
        private static string _prevWritten;   // dedup key of line before that
        private static int    _suppressed;    // lines held back in the current run
        private static bool   _alternating;   // true = A/B pattern, false = single-line repeat
        private static string _altNext;       // next expected line in the alternating pattern

        public static void InitSession(string revitVersion, string pluginVersion)
        {
            string logsFolder = GetLogsFolder();
            PurgeLogs(logsFolder, daysToKeep: 30);

            string username = Environment.UserName;
            _sessionLogPath = Path.Combine(logsFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{username}-medusa.log");

            string header =
                "=== Perseus Session Log ===" + Environment.NewLine +
                $"Start   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                $"User    : {username}" + Environment.NewLine +
                $"Machine : {Environment.MachineName}" + Environment.NewLine +
                $"Revit   : {revitVersion}" + Environment.NewLine +
                $"Plugin  : {pluginVersion}" + Environment.NewLine +
                "===========================" + Environment.NewLine +
                Environment.NewLine;

            lock (_logLock)
            {
                File.WriteAllText(_sessionLogPath, header);
            }
        }

        public static void Info(string message)
        {
            AppendToFile(message, "[INFO] ");
        }

        public static void Debug(string message)
        {
            if (Config.Instance.VerboseLogging)
                AppendToFile(message, "[DEBUG]");
        }

        public static void Warn(string message)
        {
            AppendToFile(message, "[WARN] ");
            try { SentrySdk.CaptureMessage(message, SentryLevel.Warning); } catch { }
            Console.WriteLine($"[Warn] {message}");
        }

        public static void Error(string message)
        {
            AppendToFile(message, "[ERROR]");
            try { SentrySdk.CaptureMessage(message, SentryLevel.Error); } catch { }
            Console.WriteLine($"[Error] {message}");
        }

        public static void Exception(Exception e)
        {
            AppendToFile(e.ToString(), "[ERROR]");
            try { SentrySdk.CaptureException(e); } catch { }
            Console.WriteLine($"[Exception] {e.Message}");
        }

        private static void AppendToFile(string content, string levelTag)
        {
            string key      = $"{levelTag}\t{content}";
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry  = $"{timestamp}\t{levelTag}\t{content}{Environment.NewLine}";

            string path = _sessionLogPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProjectPerseus", "medusa.log");

            lock (_logLock)
            {
                if (_suppressed > 0)
                {
                    if (_alternating)
                    {
                        if (key == _altNext)
                        {
                            _suppressed++;
                            _altNext = (_altNext == _lastWritten) ? _prevWritten : _lastWritten;
                            return;
                        }
                    }
                    else
                    {
                        if (key == _lastWritten)
                        {
                            _suppressed++;
                            return;
                        }
                    }
                    // Pattern broken — flush summary then fall through to write the new line.
                    FlushSuppressedSummary(path);
                }
                else
                {
                    if (key == _lastWritten)
                    {
                        _suppressed = 1;
                        _alternating = false;
                        return;
                    }
                    if (_prevWritten != null && key == _prevWritten)
                    {
                        _suppressed = 1;
                        _alternating = true;
                        _altNext = _lastWritten;
                        return;
                    }
                }

                _prevWritten = _lastWritten;
                _lastWritten = key;

                try
                {
                    File.AppendAllText(path, logEntry);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error saving log: {ex.Message}");
                }
            }
        }

        private static void FlushSuppressedSummary(string path)
        {
            string summary = _alternating
                ? $"(above 2 lines alternated {_suppressed} more times)"
                : $"(above repeated {_suppressed} more times)";
            string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t[INFO] \t{summary}{Environment.NewLine}";
            try { File.AppendAllText(path, entry); } catch { }
            _suppressed  = 0;
            _alternating = false;
            _altNext     = null;
        }

        private static string GetLogsFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProjectPerseus", "logs");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void PurgeLogs(string folder, int daysToKeep)
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-daysToKeep);
                foreach (string file in Directory.GetFiles(folder, "*-medusa.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
            }
            catch { }
        }
    }
}
