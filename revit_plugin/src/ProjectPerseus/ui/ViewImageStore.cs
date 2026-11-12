using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using ProjectPerseus.federator;
using ProjectPerseus.logging;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Per-user cache of review view images (federator/ViewImageCapture):
    /// %LOCALAPPDATA%\ProjectPerseus\federator\viewimages\&lt;model&gt;\&lt;view uid&gt;\&lt;stamp&gt;\ = frame.json + 512 px PNG
    /// tiles, so a thumbnail decodes only the few tiles it needs (a whole 6000 px image would be ~100 MB in memory).
    /// Tiling and cropping run on background threads; results are frozen bitmaps.
    /// </summary>
    internal static class ViewImageStore
    {
        private const int Tile = 512;
        private const int MaxCachedTiles = 64;
        private static readonly object Lock = new object();
        private static readonly LinkedList<KeyValuePair<string, BitmapSource>> Lru = new LinkedList<KeyValuePair<string, BitmapSource>>();
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>> ByPath =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>>(StringComparer.OrdinalIgnoreCase);

        internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                    "ProjectPerseus", "federator", "viewimages");

        internal static string TempDir => Path.Combine(Root, "_export");

        /// <summary>Folder key of a host model (its path, else title).</summary>
        internal static string ModelKey(string pathOrTitle)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes((pathOrTitle ?? "").ToLowerInvariant()));
                return BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ViewDir(string model, string viewUid) => Path.Combine(Root, model, Safe(viewUid));

        private static string Safe(string s) => string.Concat((s ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        /// <summary>Average seconds a capture took on this model (moving average, kept per model), 0 when unknown.</summary>
        internal static double AverageCaptureSeconds(string model)
        {
            try
            {
                var f = Path.Combine(Root, model, "capture_seconds.txt");
                return File.Exists(f) && double.TryParse(File.ReadAllText(f), System.Globalization.NumberStyles.Float,
                                                         System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 0;
            }
            catch { return 0; }
        }

        internal static void RecordCaptureSeconds(string model, double seconds)
        {
            try
            {
                double avg = AverageCaptureSeconds(model);
                avg = avg <= 0 ? seconds : 0.7 * avg + 0.3 * seconds;
                Directory.CreateDirectory(Path.Combine(Root, model));
                File.WriteAllText(Path.Combine(Root, model, "capture_seconds.txt"), avg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { /* only an estimate */ }
        }

        /// <summary>The newest complete image of a view, or null.</summary>
        internal static ViewImageFrame Load(string model, string viewUid)
        {
            try
            {
                var dir = ViewDir(model, viewUid);
                if (!Directory.Exists(dir)) return null;
                foreach (var d in Directory.GetDirectories(dir).OrderByDescending(x => x, StringComparer.Ordinal))
                {
                    var f = Path.Combine(d, "frame.json");
                    if (!File.Exists(f)) continue;
                    var frame = JsonConvert.DeserializeObject<ViewImageFrame>(File.ReadAllText(f));
                    if (frame == null) continue;
                    frame.Folder = d;
                    return frame;
                }
            }
            catch (Exception ex) { Log.Info($"[Federator] view image cache: {ex.Message}"); }
            return null;
        }

        /// <summary>Tiles the exported PNG into the cache (background), writes frame.json last, removes older images of the view.</summary>
        internal static Task<ViewImageFrame> SaveAsync(string model, ViewImageCapture.Result r)
        {
            return Task.Run(() =>
            {
                try
                {
                    string viewDir = ViewDir(model, r.Frame.ViewUid);
                    string dir = Path.Combine(viewDir, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                    Directory.CreateDirectory(dir);
                    BitmapSource src;
                    using (var fs = File.OpenRead(r.PngPath))
                        src = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    if (src.Format != PixelFormats.Bgr32) src = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
                    int w = src.PixelWidth, h = src.PixelHeight;
                    var buf = new byte[Tile * Tile * 4];
                    for (int ty = 0; ty * Tile < h; ty++)
                        for (int tx = 0; tx * Tile < w; tx++)
                        {
                            int tw = Math.Min(Tile, w - tx * Tile), th = Math.Min(Tile, h - ty * Tile);
                            src.CopyPixels(new Int32Rect(tx * Tile, ty * Tile, tw, th), buf, tw * 4, 0);
                            var tile = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgr32, null, buf, tw * 4);
                            var enc = new PngBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(tile));
                            using (var o = File.Create(Path.Combine(dir, $"t_{tx}_{ty}.png"))) enc.Save(o);
                        }
                    r.Frame.Width = w;
                    r.Frame.Height = h;
                    r.Frame.TileSize = Tile;
                    File.WriteAllText(Path.Combine(dir, "frame.json"), JsonConvert.SerializeObject(r.Frame, Formatting.Indented));
                    r.Frame.Folder = dir;
                    foreach (var old in Directory.GetDirectories(viewDir).Where(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
                        try { Directory.Delete(old, true); } catch { /* in use: next time */ }
                    try { File.Delete(r.PngPath); } catch { }
                    return r.Frame;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Federator] view image '{r.Frame?.ViewName}' not cached: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>The image region (pixels; may extend past the image = white) scaled to outW x outH, frozen.</summary>
        internal static Task<BitmapSource> CropAsync(ViewImageFrame f, double x0, double y0, double x1, double y1, int outW, int outH)
        {
            return Task.Run(() =>
            {
                try
                {
                    int ix0 = (int)Math.Floor(x0), iy0 = (int)Math.Floor(y0);
                    int w = Math.Max(1, (int)Math.Ceiling(x1) - ix0), h = Math.Max(1, (int)Math.Ceiling(y1) - iy0);
                    // Very large regions: limit the work (they're scaled down anyway).
                    if ((long)w * h > 40_000_000L) return null;
                    int stride = w * 4;
                    var buf = new byte[stride * h];
                    for (int i = 0; i < buf.Length; i++) buf[i] = 0xFF;
                    int ts = f.TileSize > 0 ? f.TileSize : Tile;
                    int cx0 = Math.Max(0, ix0), cy0 = Math.Max(0, iy0);
                    int cx1 = Math.Min(f.Width, ix0 + w), cy1 = Math.Min(f.Height, iy0 + h);
                    for (int ty = cy0 / ts; ty * ts < cy1; ty++)
                        for (int tx = cx0 / ts; tx * ts < cx1; tx++)
                        {
                            var tile = TileAt(f, tx, ty);
                            if (tile == null) continue;
                            int ax0 = Math.Max(cx0, tx * ts), ay0 = Math.Max(cy0, ty * ts);
                            int ax1 = Math.Min(cx1, tx * ts + tile.PixelWidth), ay1 = Math.Min(cy1, ty * ts + tile.PixelHeight);
                            if (ax1 <= ax0 || ay1 <= ay0) continue;
                            tile.CopyPixels(new Int32Rect(ax0 - tx * ts, ay0 - ty * ts, ax1 - ax0, ay1 - ay0), buf, stride,
                                            (ay0 - iy0) * stride + (ax0 - ix0) * 4);
                        }
                    BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, buf, stride);
                    double sx = (double)outW / w, sy = (double)outH / h;
                    if (Math.Abs(sx - 1) > 0.01 || Math.Abs(sy - 1) > 0.01) bmp = new TransformedBitmap(bmp, new ScaleTransform(sx, sy));
                    bmp.Freeze();
                    return bmp;
                }
                catch (Exception ex)
                {
                    Log.Info($"[Federator] view image crop: {ex.Message}");
                    return null;
                }
            });
        }

        private static BitmapSource TileAt(ViewImageFrame f, int tx, int ty)
        {
            string path = Path.Combine(f.Folder ?? "", $"t_{tx}_{ty}.png");
            lock (Lock)
            {
                if (ByPath.TryGetValue(path, out var node))
                {
                    Lru.Remove(node);
                    Lru.AddFirst(node);
                    return node.Value.Value;
                }
            }
            if (!File.Exists(path)) return null;
            BitmapSource bmp;
            using (var fs = File.OpenRead(path))
                bmp = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (bmp.Format != PixelFormats.Bgr32) bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
            bmp.Freeze();
            lock (Lock)
            {
                if (!ByPath.ContainsKey(path))
                {
                    ByPath[path] = Lru.AddFirst(new KeyValuePair<string, BitmapSource>(path, bmp));
                    while (Lru.Count > MaxCachedTiles)
                    {
                        ByPath.Remove(Lru.Last.Value.Key);
                        Lru.RemoveLast();
                    }
                }
            }
            return bmp;
        }
    }
}
