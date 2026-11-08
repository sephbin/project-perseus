using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using Element = Autodesk.Revit.DB.Element;
using ProjectPerseus.revit;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Bakes federator changes into views as real, printable annotations: per change visible in the view, a
    /// rectangle around its projected box - coloured detail lines (line styles "Perseus Federator - Added /
    /// Modified / Deleted") or a revision cloud on a chosen revision - a connector from the old position for moved
    /// elements, and an optional text label. Every created element is tagged (schema PerseusFederatorAnnotation:
    /// comparison, link instance, change) so a later bake can replace them and Remove can clear them. One undo step
    /// (TransactionGroup); a view that fails is rolled back and reported, the rest continue. API context.
    /// </summary>
    internal static class AnnotationBaker
    {
        private static readonly Guid TagSchemaGuid = new Guid("9F4A1B62-E07C-4D35-8B2E-61C5D0A7F3B4");
        private const string FComparison = "ComparisonId", FLink = "LinkInstanceUid", FChange = "ChangeUid";

        internal const string StylePrefix = "Perseus Federator - ";
        private const double PadPaper = 2 / 304.8, MinPaper = 6 / 304.8, TextGapPaper = 1 / 304.8, TextSizeFt = 2.0 / 304.8;

        private static readonly ViewType[] Bakeable =
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan,
            ViewType.Section, ViewType.Elevation, ViewType.Detail,
        };

        internal class Result
        {
            internal int Views, Annotations, Changes, Removed, SkippedOwned;
            internal List<string> Errors = new List<string>();
        }

        // ── Listing ──

        internal static List<ViewCandidate> Candidates(Document doc)
        {
            var list = new List<ViewCandidate>();
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                if (v.IsTemplate || !Bakeable.Contains(v.ViewType)) continue;
                string sheet = "";
                try { sheet = v.get_Parameter(BuiltInParameter.VIEWER_SHEET_NUMBER)?.AsString() ?? ""; } catch { }
                if (sheet == "---") sheet = "";
                list.Add(new ViewCandidate { Uid = v.UniqueId, Name = v.Name, ViewType = v.ViewType.ToString(), SheetNumber = sheet, Scale = v.Scale });
            }
            return list.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Revisions that can take new clouds (not issued): (UniqueId, label).</summary>
        internal static List<KeyValuePair<string, string>> OpenRevisions(Document doc)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (ElementId id in Revision.GetAllRevisionIds(doc))
            {
                if (!(doc.GetElement(id) is Revision r) || r.Issued) continue;
                list.Add(new KeyValuePair<string, string>(r.UniqueId, $"{r.SequenceNumber}. {r.Description} ({r.RevisionDate})"));
            }
            return list;
        }

        // ── Bake / remove ──

        internal static Result Bake(Document doc, RevitLinkInstance instance, ComparisonRecord record, IList<ElementChange> changes,
                                    IList<string> viewUids, BakeSettings s, Func<ElementChange, string> label, Action<string, double> progress)
        {
            var result = new Result();
            Transform toHost = instance.GetTotalTransform();
            ElementId revisionId = null;
            if (s.Style == BakeSettings.StyleRevisionCloud)
            {
                revisionId = (s.RevisionUid == null ? null : doc.GetElement(s.RevisionUid) as Revision)?.Id;
                if (revisionId == null) { result.Errors.Add("Pick an open (not issued) revision for revision clouds."); return result; }
            }

            using (var group = new TransactionGroup(doc, "Perseus: bake link changes"))
            {
                group.Start();
                var styles = EnsureStyles(doc);
                var tagSchema = TagSchema(true);
                int n = 0;
                foreach (string uid in viewUids)
                {
                    n++;
                    var view = doc.GetElement(uid) as View;
                    if (view == null || view.IsTemplate) continue;
                    progress?.Invoke($"Baking into {view.Name}…", (double)n / viewUids.Count);
                    using (var tx = new Transaction(doc, "Perseus: bake into " + view.Name))
                    {
                        try
                        {
                            tx.Start();
                            Quiet(tx);
                            if (s.ReplacePrevious) result.Removed += RemoveIn(doc, view, instance.UniqueId, result);
                            int before = result.Annotations;
                            int changed = BakeView(doc, view, instance, toHost, record, changes, s, styles, revisionId, tagSchema, label, result);
                            tx.Commit();
                            result.Views++;
                            result.Changes += changed;
                            if (result.Annotations == before && changed == 0) result.Views--; // nothing in this view
                        }
                        catch (Exception ex)
                        {
                            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                            result.Errors.Add($"{view.Name}: {ex.Message}");
                            Log.Info($"[Federator] bake into '{view.Name}' failed: {ex}");
                        }
                    }
                }
                group.Assimilate();
            }
            return result;
        }

        internal static Result Remove(Document doc, string linkInstanceUid, IList<string> viewUids)
        {
            var result = new Result();
            using (var tx = new Transaction(doc, "Perseus: remove baked link changes"))
            {
                tx.Start();
                Quiet(tx);
                foreach (string uid in viewUids)
                    if (doc.GetElement(uid) is View view) result.Removed += RemoveIn(doc, view, linkInstanceUid, result);
                tx.Commit();
            }
            return result;
        }

        private static int BakeView(Document doc, View view, RevitLinkInstance instance, Transform toHost, ComparisonRecord record,
                                    IList<ElementChange> changes, BakeSettings s, Styles styles, ElementId revisionId, Schema tagSchema,
                                    Func<ElementChange, string> label, Result result)
        {
            var extent = ViewExtent.For(view);
            if (!extent.ShowsModel) return 0;
#if REVIT2024_OR_GREATER
            var visibleIds = ReviewNavigator.VisibleLinkedIds(doc, view, instance);
#endif
            var plane = new ViewPlane(view);
            double scale = Math.Max(1, view.Scale);
            double pad = PadPaper * scale, min = MinPaper * scale, gap = TextGapPaper * scale;
            int count = 0;

            foreach (var c in changes)
            {
                bool deleted = c.Kind == ChangeKind.Deleted;
                double[] bmin = deleted ? c.OldMin : c.NewMin, bmax = deleted ? c.OldMax : c.NewMax;
                if (bmin == null || bmax == null) continue;
                ReviewNavigator.HostBox(bmin, bmax, toHost, out XYZ hmin, out XYZ hmax);
                if (!extent.Contains(hmin, hmax)) continue;
#if REVIT2024_OR_GREATER
                if (!deleted && visibleIds != null && !visibleIds.Contains(c.Id)) continue;
#endif
                plane.Rect(hmin, hmax, pad, min, out double u0, out double v0, out double u1, out double v1);
                var corners = new[] { plane.At(u0, v0), plane.At(u1, v0), plane.At(u1, v1), plane.At(u0, v1) };
                var curves = new List<Curve>();
                for (int i = 0; i < 4; i++) curves.Add(Line.CreateBound(corners[i], corners[(i + 1) % 4]));

                var created = new List<Element>();
                if (s.Style == BakeSettings.StyleRevisionCloud)
                {
                    var cloud = RevisionCloud.Create(doc, view, revisionId, curves);
                    try { cloud.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(label(c)); } catch { }
                    created.Add(cloud);
                }
                else
                {
                    var gs = styles.Line(c.Kind);
                    foreach (var curve in curves)
                    {
                        var dc = doc.Create.NewDetailCurve(view, curve);
                        if (gs != null) dc.LineStyle = gs;
                        created.Add(dc);
                    }
                }

                // Moved: connector from the old position's centre.
                if (c.Kind == ChangeKind.Modified && c.Aspects.HasFlag(ChangeAspect.Moved) && c.OldMin != null && c.OldMax != null)
                {
                    ReviewNavigator.HostBox(c.OldMin, c.OldMax, toHost, out XYZ omin, out XYZ omax);
                    XYZ from = plane.Project((omin + omax) / 2), to = plane.Project((hmin + hmax) / 2);
                    if (from.DistanceTo(to) > pad)
                    {
                        var dc = doc.Create.NewDetailCurve(view, Line.CreateBound(from, to));
                        var gs = styles.Line(ChangeKind.Modified);
                        if (gs != null) dc.LineStyle = gs;
                        created.Add(dc);
                    }
                }

                if (s.Labels)
                {
                    var typeId = styles.Text(c.Kind);
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                        created.Add(TextNote.Create(doc, view.Id, plane.At(u0, v1 + gap + TextSizeFt * scale), label(c), typeId));
                }

                foreach (var el in created) Tag(el, tagSchema, record?.Id, instance.UniqueId, c.UniqueId);
                result.Annotations += created.Count;
                count++;
            }
            return count;
        }

        private static int RemoveIn(Document doc, View view, string linkInstanceUid, Result result)
        {
            var schema = TagSchema(false);
            if (schema == null) return 0;
            var filter = new ElementMulticlassFilter(new List<Type> { typeof(CurveElement), typeof(TextNote), typeof(RevisionCloud) });
            var ids = new List<ElementId>();
            foreach (Element el in new FilteredElementCollector(doc, view.Id).WherePasses(filter))
            {
                Entity e;
                try { e = el.GetEntity(schema); } catch { continue; }
                if (e == null || !e.IsValid() || e.Get<string>(FLink) != linkInstanceUid) continue;
                if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, el.Id, out _) == CheckoutStatus.OwnedByOtherUser)
                {
                    result.SkippedOwned++;
                    continue;
                }
                ids.Add(el.Id);
            }
            if (ids.Count > 0) doc.Delete(ids);
            return ids.Count;
        }

        // ── Styles ──

        private class Styles
        {
            internal Dictionary<ChangeKind, GraphicsStyle> Lines = new Dictionary<ChangeKind, GraphicsStyle>();
            internal Dictionary<ChangeKind, ElementId> Texts = new Dictionary<ChangeKind, ElementId>();
            internal GraphicsStyle Line(ChangeKind k) => Lines.TryGetValue(k, out var g) ? g : null;
            internal ElementId Text(ChangeKind k) => Texts.TryGetValue(k, out var t) ? t : null;
        }

        private static readonly Dictionary<ChangeKind, Color> Colours = new Dictionary<ChangeKind, Color>
        {
            { ChangeKind.Added, new Color(46, 173, 75) },
            { ChangeKind.Modified, new Color(224, 130, 0) },
            { ChangeKind.Deleted, new Color(210, 58, 58) },
        };

        /// <summary>Line styles + text types per kind, created once (users may restyle them afterwards).</summary>
        private static Styles EnsureStyles(Document doc)
        {
            var styles = new Styles();
            using (var tx = new Transaction(doc, "Perseus: federator annotation styles"))
            {
                tx.Start();
                Quiet(tx);
                Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                var baseText = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType;
                var texts = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
                foreach (var kv in Colours)
                {
                    string name = StylePrefix + kv.Key;
                    try
                    {
                        Category sub = lines.SubCategories.Contains(name) ? lines.SubCategories.get_Item(name) : null;
                        if (sub == null)
                        {
                            sub = doc.Settings.Categories.NewSubcategory(lines, name);
                            sub.LineColor = kv.Value;
                            sub.SetLineWeight(6, GraphicsStyleType.Projection);
                        }
                        styles.Lines[kv.Key] = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                    }
                    catch (Exception ex) { Log.Info($"[Federator] line style '{name}': {ex.Message}"); }

                    try
                    {
                        var t = texts.FirstOrDefault(x => x.Name == name);
                        if (t == null && baseText != null)
                        {
                            t = (TextNoteType)baseText.Duplicate(name);
                            var c = kv.Value;
                            t.get_Parameter(BuiltInParameter.LINE_COLOR)?.Set(c.Red | (c.Green << 8) | (c.Blue << 16));
                            t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(TextSizeFt);
                            t.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(0); // opaque: readable over busy drawings
                        }
                        if (t != null) styles.Texts[kv.Key] = t.Id;
                    }
                    catch (Exception ex) { Log.Info($"[Federator] text type '{name}': {ex.Message}"); }
                }
                tx.Commit();
            }
            return styles;
        }

        // ── Tagging ──

        private static Schema TagSchema(bool create) => create
            ? PluginDataStorage.EnsureSchema(TagSchemaGuid, "PerseusFederatorAnnotation",
                "Project Perseus federator: an annotation baked from a link comparison.", b =>
                {
                    b.AddSimpleField(FComparison, typeof(string));
                    b.AddSimpleField(FLink, typeof(string));
                    b.AddSimpleField(FChange, typeof(string));
                })
            : Schema.Lookup(TagSchemaGuid);

        private static void Tag(Element el, Schema schema, string comparisonId, string linkUid, string changeUid)
        {
            try
            {
                var e = new Entity(schema);
                e.Set(FComparison, comparisonId ?? "");
                e.Set(FLink, linkUid ?? "");
                e.Set(FChange, changeUid ?? "");
                el.SetEntity(e);
            }
            catch (Exception ex) { Log.Info($"[Federator] couldn't tag annotation {el.Id.GetIdValue()}: {ex.Message}"); }
        }

        // ── Helpers ──

        private static void Quiet(Transaction tx)
        {
            var o = tx.GetFailureHandlingOptions();
            o.SetFailuresPreprocessor(new WarningSwallower());
            o.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(o);
        }

        private class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                a.DeleteAllWarnings();
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>The view's drawing plane: screen axes (right, up) and the sketch plane depth.</summary>
        private class ViewPlane
        {
            private readonly XYZ _o, _r, _u, _n, _planeOrigin;

            internal ViewPlane(View view)
            {
                _o = view.Origin;
                _r = view.RightDirection;
                _u = view.UpDirection;
                _n = view.ViewDirection;
                XYZ po = null;
                try { po = view.SketchPlane?.GetPlane()?.Origin; } catch { }
                _planeOrigin = po ?? _o;
            }

            internal void Rect(XYZ min, XYZ max, double pad, double minSize, out double u0, out double v0, out double u1, out double v1)
            {
                u0 = v0 = double.MaxValue;
                u1 = v1 = double.MinValue;
                for (int i = 0; i < 8; i++)
                {
                    var p = new XYZ((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z) - _o;
                    double u = p.DotProduct(_r), v = p.DotProduct(_u);
                    u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
                    v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
                }
                u0 -= pad; v0 -= pad; u1 += pad; v1 += pad;
                if (u1 - u0 < minSize) { double c = (u0 + u1) / 2; u0 = c - minSize / 2; u1 = c + minSize / 2; }
                if (v1 - v0 < minSize) { double c = (v0 + v1) / 2; v0 = c - minSize / 2; v1 = c + minSize / 2; }
            }

            internal XYZ At(double u, double v) => OnPlane(_o + _r * u + _u * v);

            internal XYZ Project(XYZ p)
            {
                var d = p - _o;
                return At(d.DotProduct(_r), d.DotProduct(_u));
            }

            private XYZ OnPlane(XYZ p) => p - _n * (p - _planeOrigin).DotProduct(_n);
        }
    }
}
