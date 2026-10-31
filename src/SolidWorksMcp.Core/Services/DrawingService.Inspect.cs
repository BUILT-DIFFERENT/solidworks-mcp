using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

public sealed class DrwIssue
{
    /// <summary>overlap | outside_frame | dangling | unattached | duplicate | missing_diameter_symbol | view_overlap | view_outside_frame.</summary>
    public required string Kind { get; init; }
    public required string Item { get; init; }
    public required string Detail { get; init; }
    public string? Other { get; init; }
}

public sealed class DrwCheckResult : ToolResult
{
    public required string Sheet { get; init; }
    public int Annotations { get; init; }
    public int Views { get; init; }
    public required double[] FrameMm { get; init; }
    public required IReadOnlyList<DrwIssue> Issues { get; init; }
    public int IssueCount { get; init; }
    public required IReadOnlyDictionary<string, int> ByKind { get; init; }
    public bool? Truncated { get; init; }
}

/// <summary>A model edge near a sheet point.</summary>
public sealed class DrwEdgeHit
{
    /// <summary>line | circle | …</summary>
    public required string Curve { get; init; }
    public double DistanceMm { get; init; }
    /// <summary>The edge's nearest sampled point in model mm: pass it as {type:'edge', at:[…]} to the other tools.</summary>
    public required double[] AtModelMm { get; init; }
    public double? LengthMm { get; init; }
    public double? RadiusMm { get; init; }
}

public sealed class DrwLocateResult : ToolResult
{
    public string? View { get; init; }
    public double[]? SheetMm { get; init; }
    /// <summary>The model point under the sheet point, on the plane through the model box centre facing the viewer (model mm).</summary>
    public double[]? ModelMm { get; init; }
    /// <summary>The direction from the model toward the viewer (model), along which ModelMm is ambiguous.</summary>
    public double[]? TowardViewer { get; init; }
    /// <summary>The views whose outline contains the sheet point.</summary>
    public IReadOnlyList<string>? ViewsHere { get; init; }
    public IReadOnlyList<string>? AnnotationsHere { get; init; }
    public IReadOnlyList<DrwEdgeHit>? Edges { get; init; }
}

public sealed partial class DrawingService
{
    // ================================================================== drw_check

    public Task<DrwCheckResult> CheckAsync(int limit, string? doc, CancellationToken ct = default)
    {
        if (limit is < 1 or > 500) throw SwException.InvalidArgument($"limit must be 1..500 (got {limit}).");
        return session.RunAsync("drw_check", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var sheet = drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, "The drawing has no current sheet.");
            var frame = Frame(sheet);
            var anns = SheetAnnotations(drw);
            var items = anns.Select(a =>
            {
                var dim = a.Dim is { } dd ? DimInfo(ctx, dd) : null;
                return new CheckItem(a.Name, a.Type, a.View, InFormat(a) ? null : Box(a), Safe(() => a.Ann.IsDangling(), false),
                    Safe(() => a.Ann.GetAttachedEntityCount3(), 0), dim?.Value, dim?.Prefix, dim?.Kind,
                    a.Owner.View.Type == (int)swDrawingViewTypes_e.swDrawingSheet);
            }).ToList();
            var diameters = new HashSet<double>();
            foreach (var m in ModelViews(drw, currentSheetOnly: true).Select(v => Safe(() => v.View.ReferencedDocument, null)).OfType<IModelDoc2>().DistinctBy(DocumentsKey))
                foreach (var dd in CylinderDiameters(m)) diameters.Add(dd);
            var issues = DrawingEdit.Check(items, frame, diameters).Select(i => new DrwIssue { Kind = i.Kind, Item = i.Item, Detail = i.Detail, Other = i.Other }).ToList();
            // Views: outlines that overlap or leave the frame.
            var views = ModelViews(drw, currentSheetOnly: true).Select(v => (v.Name, R: SheetRect.FromMeters(v.View.DrwOutline()))).Where(v => v.R is not null).ToList();
            for (int i = 0; i < views.Count; i++)
            {
                for (int j = i + 1; j < views.Count; j++)
                    if (views[i].R!.Value.Overlaps(views[j].R!.Value))
                        issues.Add(new DrwIssue { Kind = "view_overlap", Item = views[i].Name, Detail = "its outline overlaps", Other = views[j].Name });
                if (!views[i].R!.Value.Inside(frame, 0.5))
                    issues.Add(new DrwIssue { Kind = "view_outside_frame", Item = views[i].Name, Detail = $"outline [{Fmt(views[i].R!.Value.ToArray())}] mm leaves the frame" });
            }
            var byKind = issues.GroupBy(i => i.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
            return new DrwCheckResult
            {
                Doc = ctx.Title(d), Sheet = sheet.GetName(), Annotations = anns.Count, Views = views.Count, FrameMm = frame.ToArray(),
                Issues = issues.Take(limit).ToList(), IssueCount = issues.Count, ByKind = byKind, Truncated = issues.Count > limit ? true : null,
            };
        }, RunOptions.Read(doc) with { Timeout = Longer(session, 90) }, ct);
    }

    private static string DocumentsKey(IModelDoc2 m) => Safe(() => m.GetPathName(), "") ?? "";

    /// <summary>The diameters (mm) of the cylindrical faces of a part (assemblies: none; up to 3000 faces).</summary>
    private static IEnumerable<double> CylinderDiameters(IModelDoc2 m)
    {
        if (m.GetType() != (int)swDocumentTypes_e.swDocPART) yield break;
        int n = 0;
        foreach (var body in ((IPartDoc)m).Bodies(swBodyType_e.swSolidBody))
            foreach (var f in ComArgs.Objects<IFace2>(Safe(() => (object?)body.GetFaces(), null)))
            {
                if (++n > 3000) yield break;
                FaceGeom? g = null;
                try { g = Topology.Describe(f); }
                catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* skip */ }
                if (g is { Surface: "cylinder", Radius: double r }) yield return Math.Round(2 * r * 1000, 6);
            }
    }

    // ================================================================== drw_locate

    public Task<DrwLocateResult> LocateAsync(string? view, double[]? sheetMm, double[]? modelMm, int edges, string? doc, CancellationToken ct = default)
    {
        if ((sheetMm is null) == (modelMm is null)) throw SwException.InvalidArgument("Pass sheetMm (what is at this sheet point) or modelMm (where this model point is on the sheet).");
        if (sheetMm is not null) Sheet2(sheetMm, "sheetMm");
        if (modelMm is not null)
        {
            Units.PointM(modelMm, "modelMm");
            if (view is null) throw SwException.InvalidArgument("modelMm needs 'view': the view whose model it is in.");
        }
        if (edges is < 0 or > 20) throw SwException.InvalidArgument($"edges must be 0..20 (got {edges}).");
        return session.RunAsync("drw_locate", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            if (modelMm is not null)
            {
                var hit = FindView(ctx, drw, view, "view");
                var s = ModelToSheet(hit, modelMm, "modelMm");
                var axes = DrawingLayout.AxesOf(hit.View.DrwModelToSheet());
                bool inside = Outline(hit).Contains(s.X, s.Y, 0.5);
                if (!inside) ctx.Warn($"[{Fmt([s.X, s.Y])}] mm lies outside the outline of '{hit.Name}'.");
                return new DrwLocateResult
                {
                    Doc = ctx.Title(d), View = hit.Name, SheetMm = [Math.Round(s.X, 3), Math.Round(s.Y, 3)], ModelMm = modelMm,
                    TowardViewer = Units.Unit(axes.Toward),
                };
            }
            var p = Sheet2(sheetMm, "sheetMm");
            var views = ModelViews(drw, currentSheetOnly: true);
            var here = views.Where(v => SheetRect.FromMeters(v.View.DrwOutline()) is SheetRect r && r.Contains(p.X, p.Y)).ToList();
            ViewHit? target = view is not null ? FindView(ctx, drw, view, "view")
                : here.OrderBy(v => SheetRect.FromMeters(v.View.DrwOutline())!.Value.W * SheetRect.FromMeters(v.View.DrwOutline())!.Value.H).FirstOrDefault();
            var annHere = SheetAnnotations(drw).Where(a => Box(a) is SheetRect b && b.Contains(p.X, p.Y, 0.5)).Select(a => a.Name).Take(12).ToList();
            double[]? model = null, toward = null;
            List<DrwEdgeHit>? edgeHits = null;
            if (target is not null && target.View.DrwModelToSheet() is { Length: >= 13 } xf)
            {
                var t = new Xform(xf);
                var inv = t.Inverse();
                var axes = DrawingLayout.AxesOf(xf);
                toward = Units.Unit(axes.Toward);
                // The sheet point on the plane through the model box centre (depth along the view direction is unknown).
                double depth = 0;
                if (target.View.ReferencedDocument is IModelDoc2 m && ModelBox(m) is { } box)
                    depth = t.ApplyPoint(new Vec3((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2)).Z;
                var mp = inv.ApplyPoint(new Vec3(Units.M(p.X), Units.M(p.Y), depth));
                model = Units.Mm(mp);
                if (edges > 0) edgeHits = EdgesNear(target, t, p, edges);
            }
            return new DrwLocateResult
            {
                Doc = ctx.Title(d), View = target?.Name, SheetMm = [p.X, p.Y], ModelMm = model, TowardViewer = toward,
                ViewsHere = here.Count > 0 ? here.Select(v => v.Name).ToList() : null,
                AnnotationsHere = annHere.Count > 0 ? annHere : null, Edges = edgeHits is { Count: > 0 } ? edgeHits : null,
            };
        }, RunOptions.Read(doc) with { Timeout = Longer(session, 60) }, ct);
    }

    /// <summary>
    /// The visible model edges of a part view nearest a sheet point (mm on the sheet), each sampled at 17 points along
    /// its curve. Assembly views report component edges in part space, so they are left out (with a warning).
    /// </summary>
    private static List<DrwEdgeHit> EdgesNear(ViewHit hit, Xform t, (double X, double Y) p, int count)
    {
        if (hit.View.ReferencedDocument is not IModelDoc2 m || m.GetType() != (int)swDocumentTypes_e.swDocPART) return [];
        var visible = ComArgs.Objects<IEdge>(Safe(() => (object?)hit.View.GetVisibleEntities2(null, (int)swViewEntityType_e.swViewEntityType_Edge), null));
        var hits = new List<(double D, DrwEdgeHit H)>();
        foreach (var e in visible.Take(5000))
        {
            try
            {
                var cp = e.CurveParams();
                if (cp.Length < 8) continue;
                double t0 = cp[6], t1 = cp[7], best = double.MaxValue;
                Vec3 bestP = default;
                for (int i = 0; i <= 16; i++)
                {
                    var q = e.EvalAt(t0 + (t1 - t0) * i / 16);
                    if (q.Length < 3) continue;
                    var mp = Vec3.From(q);
                    var s = t.ApplyPoint(mp);
                    double dist = Math.Sqrt(Math.Pow(s.X * 1000 - p.X, 2) + Math.Pow(s.Y * 1000 - p.Y, 2));
                    if (dist < best) { best = dist; bestP = mp; }
                }
                if (best == double.MaxValue) continue;
                var g = Topology.Describe(e);
                hits.Add((best, new DrwEdgeHit
                {
                    Curve = g.Curve, DistanceMm = Math.Round(best, 3), AtModelMm = Units.Mm(bestP), LengthMm = Units.Mm(g.Length),
                    RadiusMm = g.Radius is double r ? Units.Mm(r) : null,
                }));
            }
            catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* skip */ }
        }
        return hits.OrderBy(h => h.D).Take(count).Select(h => h.H).ToList();
    }
}
