using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Captures an image of a review view (federator/ReviewViews) for the panel's thumbnails, with the mapping from
    /// host model points to image pixels (federator/ViewImageFrame).
    ///
    /// The view is duplicated (no detailing) inside a TransactionGroup that is always rolled back, so the model is left
    /// untouched: the duplicate gets a crop region we know (the view's own crop when it's cropped, else the area of the
    /// changes it shows, padded), crop boundary hidden, annotation crop on, then Document.ExportImage writes a PNG.
    /// Revit exports the crop region (or the annotation crop around it); which one is decided from the image's aspect
    /// ratio. API context, no open transaction; takes seconds on a large model.
    /// </summary>
    internal static class ViewImageCapture
    {
        private const double Pad = 3000 / 304.8;          // around the changes (uncropped views)
        private const double MinSide = 10000 / 304.8;     // at least 10 m across
        private const double MmPerPixel = 20;             // target resolution

        internal class Result
        {
            internal string PngPath;
            internal ViewImageFrame Frame;
        }

        /// <param name="boxes">Host boxes (min, max) of the changes this view should show; used when the view isn't cropped.</param>
        internal static Result Capture(Document doc, View view, IList<KeyValuePair<double[], double[]>> boxes, int maxPixels, string tempDir)
        {
            if (doc == null || view == null) return null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Directory.CreateDirectory(tempDir);
            bool cropped = view.CropBoxActive;
            string cropSig = cropped ? CropSignature(view) : "";
            if (!cropped && (boxes == null || boxes.Count == 0)) return null;

            using (var group = new TransactionGroup(doc, "Perseus: review view image"))
            {
                group.Start();
                try
                {
                    View dup;
                    using (var tx = new Transaction(doc, "Perseus: review view image"))
                    {
                        tx.Start();
                        var fo = tx.GetFailureHandlingOptions();
                        fo.SetFailuresPreprocessor(new Quiet());
                        tx.SetFailureHandlingOptions(fo);
                        if (!view.CanViewBeDuplicated(ViewDuplicateOption.Duplicate)) { tx.RollBack(); return null; }
                        dup = (View)doc.GetElement(view.Duplicate(ViewDuplicateOption.Duplicate));
                        if (!cropped)
                        {
                            try { dup.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.Set(ElementId.InvalidElementId); } catch { }
                            var crop = dup.CropBox;
                            Transform toLocal = (crop.Transform ?? Transform.Identity).Inverse;
                            double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
                            foreach (var b in boxes)
                                for (int i = 0; i < 8; i++)
                                {
                                    var p = toLocal.OfPoint(new XYZ((i & 1) == 0 ? b.Key[0] : b.Value[0], (i & 2) == 0 ? b.Key[1] : b.Value[1], (i & 4) == 0 ? b.Key[2] : b.Value[2]));
                                    u0 = Math.Min(u0, p.X); v0 = Math.Min(v0, p.Y); u1 = Math.Max(u1, p.X); v1 = Math.Max(v1, p.Y);
                                }
                            Grow(ref u0, ref u1);
                            Grow(ref v0, ref v1);
                            crop.Min = new XYZ(u0, v0, crop.Min.Z);
                            crop.Max = new XYZ(u1, v1, crop.Max.Z);
                            dup.CropBox = crop;
                        }
                        dup.CropBoxActive = true;
                        dup.CropBoxVisible = false;
                        try
                        {
                            var ac = dup.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                            if (ac != null && !ac.IsReadOnly) ac.Set(1);
                        }
                        catch { /* 3D views have no annotation crop */ }
                        tx.Commit();
                    }

                    var box = dup.CropBox;
                    Transform t = box.Transform ?? Transform.Identity;
                    double cu0 = Math.Min(box.Min.X, box.Max.X), cu1 = Math.Max(box.Min.X, box.Max.X);
                    double cv0 = Math.Min(box.Min.Y, box.Max.Y), cv1 = Math.Max(box.Min.Y, box.Max.Y);
                    double l = 0, r = 0, bo = 0, to = 0;
                    try
                    {
                        var m = dup.GetCropRegionShapeManager();
                        double s = Math.Max(1, dup.Scale);
                        l = m.LeftAnnotationCropOffset * s; r = m.RightAnnotationCropOffset * s;
                        bo = m.BottomAnnotationCropOffset * s; to = m.TopAnnotationCropOffset * s;
                    }
                    catch { /* no annotation crop */ }

                    double wFt = cu1 - cu0, hFt = cv1 - cv0;
                    bool wide = wFt >= hFt;
                    int px = (int)Math.Max(1200, Math.Min(Math.Max(1200, maxPixels), Math.Max(wFt, hFt) * 304.8 / MmPerPixel));
                    string png = Export(doc, dup, tempDir, px, wide);
                    if (png == null) return null;

                    int w, h;
                    using (var fs = File.OpenRead(png))
                    {
                        var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(fs, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                                                                                   System.Windows.Media.Imaging.BitmapCacheOption.None);
                        w = dec.Frames[0].PixelWidth;
                        h = dec.Frames[0].PixelHeight;
                    }

                    // Crop region or annotation crop: whichever matches the image's aspect ratio.
                    double imgAspect = (double)w / h;
                    double errCrop = Math.Abs(Math.Log(imgAspect / (wFt / hFt)));
                    double aw = wFt + l + r, ah = hFt + bo + to;
                    double errAnno = Math.Abs(Math.Log(imgAspect / (aw / ah)));
                    bool anno = (l + r + bo + to) > 1e-6 && errAnno < errCrop;
                    var frame = new ViewImageFrame
                    {
                        ViewUid = view.UniqueId, ViewName = view.Name, ViewType = view.ViewType.ToString(), CapturedUtc = DateTime.UtcNow,
                        CropSignature = cropSig,
                        Origin = new[] { t.Origin.X, t.Origin.Y, t.Origin.Z },
                        Right = new[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z },
                        Up = new[] { t.BasisY.X, t.BasisY.Y, t.BasisY.Z },
                        U0 = anno ? cu0 - l : cu0, U1 = anno ? cu1 + r : cu1,
                        V0 = anno ? cv0 - bo : cv0, V1 = anno ? cv1 + to : cv1,
                        Width = w, Height = h,
                        LinkVersions = LinkVersions(doc),
                    };
                    double err = anno ? errAnno : errCrop;
                    Log.Info($"[Federator] review view image '{view.Name}': {w}x{h} px, {wFt * 0.3048:F1} x {hFt * 0.3048:F1} m" +
                             $"{(cropped ? " (view crop)" : " (around changes)")}, {(anno ? "annotation crop" : "crop region")} " +
                             $"(aspect off {err * 100:F1} %), {clock.ElapsedMilliseconds} ms");
                    if (err > 0.05) Log.Warn($"[Federator] review view image '{view.Name}': image aspect doesn't match its crop ({err * 100:F1} %); change marks may be offset.");
                    return new Result { PngPath = png, Frame = frame };
                }
                finally
                {
                    if (group.HasStarted()) group.RollBack();
                }
            }
        }

        private static string Export(Document doc, View dup, string dir, int pixels, bool wide)
        {
            foreach (var f in Directory.GetFiles(dir, "*.png")) { try { File.Delete(f); } catch { } }
            var o = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(dir, "view"),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = wide ? FitDirectionType.Horizontal : FitDirectionType.Vertical,
                PixelSize = pixels,
            };
            o.SetViewsAndSheets(new List<ElementId> { dup.Id });
            doc.ExportImage(o);
            return Directory.GetFiles(dir, "*.png").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }

        private static void Grow(ref double a, ref double b)
        {
            a -= Pad; b += Pad;
            if (b - a < MinSide) { double c = (a + b) / 2; a = c - MinSide / 2; b = c + MinSide / 2; }
        }

        internal static string CropSignature(View v)
        {
            try
            {
                var b = v.CropBox;
                var o = b.Transform?.Origin ?? XYZ.Zero;
                return $"{b.Min.X:F1},{b.Min.Y:F1},{b.Max.X:F1},{b.Max.Y:F1}@{o.X:F1},{o.Y:F1},{o.Z:F1}";
            }
            catch { return ""; }
        }

        internal static Dictionary<string, string> LinkVersions(Document doc)
        {
            var map = new Dictionary<string, string>();
            foreach (RevitLinkInstance inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                try
                {
                    var ld = inst.GetLinkDocument();
                    var dv = ld == null ? null : Document.GetDocumentVersion(ld);
                    map[inst.UniqueId] = dv?.VersionGUID.ToString() ?? "";
                }
                catch { map[inst.UniqueId] = ""; }
            }
            return map;
        }

        private class Quiet : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                a.DeleteAllWarnings();
                return FailureProcessingResult.Continue;
            }
        }
    }
}
