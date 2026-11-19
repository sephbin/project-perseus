using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectPerseus.logging
{
    /// <summary>
    /// Zips the Perseus session logs (the last 30 days that Log keeps), the recent Revit journals (RevitJournals below,
    /// journals/) and a Windows Event Log crash snapshot (WindowsEventSnapshot below, events/) into the user's
    /// Downloads folder as perseus-logs-&lt;user&gt;-&lt;yyyyMMdd-HHmmss&gt;.zip, so users can send them in one file.
    /// </summary>
    public static class LogPackager
    {
        public class Result
        {
            public string ZipPath;
            public int Files;
            public int Journals;
            public long Bytes;
        }

        public static Result Package(string revitVersion, string pluginVersion)
        {
            Log.Info("[LogPackager] packaging logs");
            string folder = Log.LogsFolder;
            string user = Sanitise(Environment.UserName);
            string zipPath = UniquePath(Path.Combine(DownloadsFolder(), $"perseus-logs-{user}-{DateTime.Now:yyyyMMdd-HHmmss}.zip"));

            var files = Directory.GetFiles(folder, "*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var result = new Result { ZipPath = zipPath };

            // Revit journals and a Windows Event Log snapshot: a crash outside Perseus' own code (e.g. GDI handle
            // exhaustion, a native fault) leaves no trace in the Perseus log, only there.
            var journals = new List<RevitJournals.Journal>();
            try { journals = RevitJournals.Recent(JournalDays, MaxJournals); }
            catch (Exception e) { Log.Warn($"[LogPackager] journal lookup failed: {e.Message}"); }
            string eventsDir = Path.Combine(Path.GetTempPath(), "perseus-events-" + Guid.NewGuid().ToString("N"));
            var eventNotes = new List<string>();
            try { eventNotes = WindowsEventSnapshot.Write(eventsDir, EventDays); }
            catch (Exception e) { eventNotes.Add("event snapshot failed: " + e.Message); }
            int journalFiles = 0;

            using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    if (AddFile(zip, file, Path.GetFileName(file), result)) result.Files++;
                }
                foreach (var j in journals)
                {
                    if (AddFile(zip, j.Path, j.EntryName, result)) journalFiles++;
                }
                if (Directory.Exists(eventsDir))
                    foreach (var file in Directory.GetFiles(eventsDir))
                        AddFile(zip, file, "events/" + Path.GetFileName(file), result);

                var info = zip.CreateEntry("perseus-info.txt").Open();
                using (var w = new StreamWriter(info))
                {
                    w.WriteLine($"Packaged : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    w.WriteLine($"User     : {Environment.UserName}");
                    w.WriteLine($"Machine  : {Environment.MachineName}");
                    w.WriteLine($"Revit    : {revitVersion}");
                    w.WriteLine($"Plugin   : {pluginVersion}");
                    w.WriteLine($"Logs     : {result.Files} file(s) from {folder}");
                    w.WriteLine($"Journals : {journalFiles} file(s), last {JournalDays} days (journals/)");
                    foreach (var j in journals.Where(j => j.EntryName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                        w.WriteLine($"           {j.EntryName}  (last written {j.Modified:yyyy-MM-dd HH:mm:ss})");
                    w.WriteLine($"Events   : (events/)");
                    foreach (var n in eventNotes) w.WriteLine($"           {n}");
                }
            }
            try { if (Directory.Exists(eventsDir)) Directory.Delete(eventsDir, true); }
            catch { /* temp folder; Windows cleans it eventually */ }

            result.Journals = journalFiles;
            Log.Info($"[LogPackager] wrote {result.Files} log(s), {journalFiles} journal file(s), events ({string.Join("; ", eventNotes)}), " +
                     $"{result.Bytes / 1024} KB uncompressed, to {zipPath}");
            return result;
        }

        private const int JournalDays = 3;
        private const int MaxJournals = 12;
        private const int EventDays = 7;
        private const long MaxFileBytes = 300L * 1024 * 1024; // skip anything absurd (a runaway journal)

        // Adds one file; the file may still be written (session log, the running Revit's journal), so share it.
        private static bool AddFile(ZipArchive zip, string path, string entryName, Result result)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes)
                {
                    Log.Warn($"[LogPackager] skipped {entryName}: {info.Length / (1024 * 1024)} MB");
                    return false;
                }
                using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open())
                {
                    src.CopyTo(dst);
                    result.Bytes += src.Length;
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"[LogPackager] could not add {entryName}: {e.Message}");
                return false;
            }
        }

        private static string Sanitise(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "user" : s;
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string p = Path.Combine(dir, $"{name}-{i}{ext}");
                if (!File.Exists(p)) return p;
            }
        }

        // The real Downloads folder (it can be redirected, e.g. to OneDrive); %USERPROFILE%\Downloads otherwise.
        private static string DownloadsFolder()
        {
            try
            {
                var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out IntPtr p) == 0)
                {
                    try
                    {
                        string path = Marshal.PtrToStringUni(p);
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
                    }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            catch (Exception e) { Log.Info($"[LogPackager] Downloads lookup failed: {e.Message}"); }

            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags,
                                                       IntPtr hToken, out IntPtr ppszPath);
    
        /// <summary>
        /// Finds the recent Revit journals for LogPackager: journal.NNNN.txt plus its journal.NNNN.worker*.log files, from
        /// every installed Revit version (%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 20xx\Journals). A journal records
        /// what Revit did up to a crash (exception code, GDI / memory usage, the add-in external events that ran), which
        /// the Perseus log can't see (2026-11-19: a GDI exhaustion crash was only visible in the journal).
        /// </summary>
        private static class RevitJournals
        {
            public class Journal
            {
                public string Path;
                public string EntryName;   // "journals/Autodesk Revit 2026/journal.0526.txt"
                public DateTime Modified;
            }

            private static readonly Regex JournalName = new Regex(@"^journal\.(\d+)\.txt$", RegexOptions.IgnoreCase);
            private static readonly Regex WorkerName = new Regex(@"^journal\.(\d+)\.worker\d*\.log$", RegexOptions.IgnoreCase);

            /// <summary>Journals written in the last <paramref name="days"/> days (newest first, at most <paramref name="max"/>)
            /// and their worker logs.</summary>
            public static List<Journal> Recent(int days, int max)
            {
                var result = new List<Journal>();
                string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autodesk", "Revit");
                if (!Directory.Exists(root)) return result;

                DateTime since = DateTime.Now.AddDays(-days);
                var journals = new List<(FileInfo file, string version)>();
                var workers = new List<(FileInfo file, string version, string number)>();
                foreach (var versionDir in SafeDirs(root, "Autodesk Revit *"))
                {
                    string dir = System.IO.Path.Combine(versionDir, "Journals");
                    if (!Directory.Exists(dir)) continue;
                    string version = System.IO.Path.GetFileName(versionDir);
                    foreach (var f in SafeFiles(dir))
                    {
                        if (f.LastWriteTime < since) continue;
                        if (JournalName.IsMatch(f.Name)) journals.Add((f, version));
                        else
                        {
                            var m = WorkerName.Match(f.Name);
                            if (m.Success) workers.Add((f, version, m.Groups[1].Value));
                        }
                    }
                }

                foreach (var (file, version) in journals.OrderByDescending(j => j.file.LastWriteTime).Take(max))
                {
                    result.Add(new Journal { Path = file.FullName, EntryName = $"journals/{version}/{file.Name}", Modified = file.LastWriteTime });
                    string number = JournalName.Match(file.Name).Groups[1].Value;
                    foreach (var w in workers.Where(w => w.version == version && w.number == number))
                        result.Add(new Journal { Path = w.file.FullName, EntryName = $"journals/{version}/{w.file.Name}", Modified = w.file.LastWriteTime });
                }
                return result;
            }

            private static IEnumerable<string> SafeDirs(string root, string pattern)
            {
                try { return Directory.GetDirectories(root, pattern); }
                catch (Exception e) { Log.Info($"[LogPackager] cannot list {root}: {e.Message}"); return new string[0]; }
            }

            private static IEnumerable<FileInfo> SafeFiles(string dir)
            {
                try { return new DirectoryInfo(dir).GetFiles(); }
                catch (Exception e) { Log.Info($"[LogPackager] cannot list {dir}: {e.Message}"); return new FileInfo[0]; }
            }
        }

        /// <summary>
        /// Snapshot of the Windows Event Log for LogPackager, via wevtutil (in every Windows, works without admin rights
        /// and needs no extra assembly on .NET Framework or .NET 8):
        ///  - crashes.evtx: Application log crash / hang events (Application Error, Application Hang, .NET Runtime,
        ///    Windows Error Reporting) of the last N days, opens in Event Viewer;
        ///  - crashes.txt: the same as text, Revit / Autodesk / Perseus events only (readable without Event Viewer);
        ///  - low-memory.txt: System log Resource-Exhaustion-Detector events (Windows ran low on virtual memory).
        /// Each step is best effort; failures are reported in the returned notes, never thrown.
        /// </summary>
        private static class WindowsEventSnapshot
        {
            private const int TimeoutMs = 20000;

            private static string CrashQuery(int days) =>
                "*[System[Provider[@Name='Application Error' or @Name='Application Hang' or @Name='.NET Runtime' or " +
                $"@Name='Windows Error Reporting'] and TimeCreated[timediff(@SystemTime) <= {days * 86400000L}]]]";

            private static string LowMemoryQuery(int days) =>
                "*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and " +
                $"TimeCreated[timediff(@SystemTime) <= {days * 86400000L}]]]";

            private static readonly string[] Relevant = { "Revit", "Autodesk", "ProjectPerseus", "RevitWorker" };

            /// <summary>Writes the snapshot files into <paramref name="folder"/>; returns one note line per file.</summary>
            public static List<string> Write(string folder, int days)
            {
                var notes = new List<string>();
                Directory.CreateDirectory(folder);

                string evtx = Path.Combine(folder, "crashes.evtx");
                var (ok, _, err) = Run($"epl Application \"{evtx}\" /q:\"{CrashQuery(days)}\" /ow:true");
                notes.Add(ok && File.Exists(evtx) ? $"crashes.evtx: Application log crash events, last {days} days"
                                                 : $"crashes.evtx: not exported ({err})");

                var (ok2, text, err2) = Run($"qe Application /q:\"{CrashQuery(days)}\" /f:text /rd:true /c:500");
                if (ok2)
                {
                    var events = SplitEvents(text);
                    var mine = events.Where(e => Relevant.Any(r => e.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                    File.WriteAllText(Path.Combine(folder, "crashes.txt"),
                        $"Application log crash / hang events mentioning Revit, Autodesk or Perseus, last {days} days, newest first.\r\n" +
                        $"{mine.Count} of {events.Count} crash events (all of them are in crashes.evtx).\r\n\r\n" +
                        string.Join("\r\n", mine), Encoding.UTF8);
                    notes.Add($"crashes.txt: {mine.Count} Revit-related of {events.Count} crash event(s)");
                }
                else notes.Add($"crashes.txt: not read ({err2})");

                var (ok3, low, err3) = Run($"qe System /q:\"{LowMemoryQuery(days)}\" /f:text /rd:true /c:50");
                if (ok3)
                {
                    int n = SplitEvents(low).Count;
                    File.WriteAllText(Path.Combine(folder, "low-memory.txt"),
                        $"System log low virtual memory warnings (Resource-Exhaustion-Detector), last {days} days: {n}\r\n\r\n" + low, Encoding.UTF8);
                    notes.Add($"low-memory.txt: {n} low memory warning(s)");
                }
                else notes.Add($"low-memory.txt: not read ({err3})");

                return notes;
            }

            // wevtutil /f:text prints each event as "Event[n]:" followed by indented fields.
            private static List<string> SplitEvents(string text)
            {
                var events = new List<string>();
                if (string.IsNullOrWhiteSpace(text)) return events;
                var current = new StringBuilder();
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    if (line.StartsWith("Event[", StringComparison.Ordinal) && current.Length > 0)
                    {
                        events.Add(current.ToString());
                        current.Clear();
                    }
                    current.Append(line).Append("\r\n");
                }
                if (current.ToString().Trim().Length > 0) events.Add(current.ToString());
                return events;
            }

            private static (bool ok, string output, string error) Run(string args)
            {
                try
                {
                    var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"), args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        var stdout = p.StandardOutput.ReadToEndAsync();
                        var stderr = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(TimeoutMs))
                        {
                            try { p.Kill(); } catch { /* already gone */ }
                            return (false, "", "timed out");
                        }
                        string err = stderr.Result.Trim();
                        return (p.ExitCode == 0, stdout.Result, p.ExitCode == 0 ? "" : $"exit {p.ExitCode}: {err}");
                    }
                }
                catch (Exception e)
                {
                    return (false, "", e.Message);
                }
            }
        }
    }
}
