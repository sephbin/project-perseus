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
    // Repeat suppression: consecutive identical lines, or an A/B/A/B alternating pair,
    // are collapsed by rewriting the last written line(s) in-place with an xN suffix
    // (e.g. "Sync Caption Changed: Updating References x25726"). No separate summary
    // lines are emitted. The file always reflects the current count.
    //
    // File path: %AppData%\ProjectPerseus\logs\<timestamp>-<user>-medusa.log (per session,
    // created by InitSession). Falls back to %AppData%\ProjectPerseus\medusa.log if a call
    // arrives before InitSession (e.g. a static initializer logging something).
    public static class Log
    {
        private static string _sessionLogPath;
        private static readonly object _logLock = new object();
        private static readonly System.Text.UTF8Encoding _enc = new System.Text.UTF8Encoding(false);

        // Dedup state. _count == 0 means nothing written yet in this session.
        // _count == 1 means last line appeared exactly once (no suffix).
        // _count >= 2 means a repeat/alternating run is active.
        private static string _lastKey;        // dedup key of last written line
        private static string _lastLineBase;   // formatted line text without newline or suffix
        private static long   _lastLineStart;  // byte offset in file where last line begins
        private static string _prevKey;
        private static string _prevLineBase;
        private static long   _prevLineStart;
        private static int    _count;
        private static bool   _alternating;    // true = A/B pair, false = single-line repeat
        private static string _altNext;        // next expected key in the alternating pattern

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
                _lastKey = null; _lastLineBase = null; _lastLineStart = 0;
                _prevKey = null; _prevLineBase = null; _prevLineStart = 0;
                _count = 0; _alternating = false; _altNext = null;
                File.WriteAllText(_sessionLogPath, header);
            }
        }

        public static void Info(string message)      => AppendToFile(message, "[INFO] ");
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
        public static void Debug(string message)
        {
            if (Config.Instance.VerboseLogging)
                AppendToFile(message, "[DEBUG]");
        }

        private static void AppendToFile(string content, string levelTag)
        {
            string key      = $"{levelTag}\t{content}";
            string lineBase = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{levelTag}\t{content}";

            string path = _sessionLogPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProjectPerseus", "medusa.log");

            lock (_logLock)
            {
                if (_count > 1)
                {
                    // Active run — extend it or break out.
                    bool continues = _alternating ? key == _altNext : key == _lastKey;
                    if (continues)
                    {
                        _count++;
                        if (_alternating)
                            _altNext = (_altNext == _lastKey) ? _prevKey : _lastKey;
                        RewriteLastLines(path);
                        return;
                    }
                    // Pattern broken — fall through to write the new line.
                }
                else if (_count == 1)
                {
                    // Check whether a new run is starting.
                    if (key == _lastKey)
                    {
                        _count = 2;
                        _alternating = false;
                        RewriteLastLines(path);
                        return;
                    }
                    if (_prevKey != null && key == _prevKey)
                    {
                        _count = 2;
                        _alternating = true;
                        _altNext = _lastKey;
                        RewriteLastLines(path);
                        return;
                    }
                }

                // Write this as a new unique line.
                try
                {
                    long startPos = File.Exists(path) ? new FileInfo(path).Length : 0;
                    File.AppendAllText(path, lineBase + Environment.NewLine, _enc);

                    _prevKey       = _lastKey;
                    _prevLineBase  = _lastLineBase;
                    _prevLineStart = _lastLineStart;
                    _lastKey       = key;
                    _lastLineBase  = lineBase;
                    _lastLineStart = startPos;
                    _count         = 1;
                    _alternating   = false;
                    _altNext       = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error saving log: {ex.Message}");
                }
            }
        }

        // Truncates the file back to the start of the last written line (or pair for
        // alternating) and rewrites with the current xN count appended.
        private static void RewriteLastLines(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
                {
                    fs.SetLength(_alternating ? _prevLineStart : _lastLineStart);
                    fs.Seek(0, SeekOrigin.End);

                    if (_alternating)
                    {
                        Write(fs, $"{_prevLineBase} x{_count}");
                        _lastLineStart = fs.Position;
                        Write(fs, $"{_lastLineBase} x{_count}");
                    }
                    else
                    {
                        Write(fs, $"{_lastLineBase} x{_count}");
                    }
                }
            }
            catch { }
        }

        private static void Write(FileStream fs, string line)
        {
            var bytes = _enc.GetBytes(line + Environment.NewLine);
            fs.Write(bytes, 0, bytes.Length);
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
