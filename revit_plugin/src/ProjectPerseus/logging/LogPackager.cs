using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;

namespace ProjectPerseus.logging
{
    /// <summary>
    /// Zips the Perseus session logs (the last 30 days that Log keeps) into the user's Downloads folder as
    /// perseus-logs-&lt;user&gt;-&lt;yyyyMMdd-HHmmss&gt;.zip, so users can send them in one file.
    /// </summary>
    public static class LogPackager
    {
        public class Result
        {
            public string ZipPath;
            public int Files;
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
            using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    try
                    {
                        // The session log is still being appended to: share read/write so we never block the logger.
                        using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var dst = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal).Open())
                        {
                            src.CopyTo(dst);
                            result.Bytes += src.Length;
                        }
                        result.Files++;
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"[LogPackager] could not add {Path.GetFileName(file)}: {e.Message}");
                    }
                }

                var info = zip.CreateEntry("perseus-info.txt").Open();
                using (var w = new StreamWriter(info))
                {
                    w.WriteLine($"Packaged : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    w.WriteLine($"User     : {Environment.UserName}");
                    w.WriteLine($"Machine  : {Environment.MachineName}");
                    w.WriteLine($"Revit    : {revitVersion}");
                    w.WriteLine($"Plugin   : {pluginVersion}");
                    w.WriteLine($"Logs     : {result.Files} file(s) from {folder}");
                }
            }
            Log.Info($"[LogPackager] wrote {result.Files} log(s) ({result.Bytes / 1024} KB uncompressed) to {zipPath}");
            return result;
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
    }
}
