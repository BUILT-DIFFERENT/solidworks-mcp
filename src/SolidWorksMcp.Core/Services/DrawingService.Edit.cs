using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== DTOs (editing existing annotations)

/// <summary>A dimension's display settings, as drw_annotation_list reads them.</summary>
public sealed class DrwDimensionInfo
{
    /// <summary>linear | horizontal | vertical | angular | radial | diameter | chamfer | ordinate | arc_length | …</summary>
    public required string Kind { get; init; }
    /// <summary>The full name ("RD1@Drawing View1"; a model dimension: "D1@Sketch1@block.Part").</summary>
    public required string FullName { get; init; }
    public double Value { get; init; }
    /// <summary>mm | deg.</summary>
    public required string Unit { get; init; }
    public string? Prefix { get; init; }
    public string? Suffix { get; init; }
    public string? Above { get; init; }
    public string? Below { get; init; }
    /// <summary>none | basic | bilateral | limit | symmetric | min | max | fit | …</summary>
    public string? Tolerance { get; init; }
    /// <summary>Upper and lower deviations (mm or deg) for bilateral, limit, symmetric, min and max.</summary>
    public double? PlusMm { get; init; }
    public double? MinusMm { get; init; }
    public string? Hole { get; init; }
    public string? Shaft { get; init; }
    /// <summary>Decimal places of the value and of the tolerance (-1: the document's setting).</summary>
    public int? Precision { get; init; }
    public int? TolerancePrecision { get; init; }
    /// <summary>Shown in parentheses (a reference dimension's usual look).</summary>
    public bool? Parentheses { get; init; }
    /// <summary>A driven (reference) dimension, not a model dimension.</summary>
    public bool? Reference { get; init; }
    /// <summary>The model document that owns a model dimension (changing its tolerance changes that model).</summary>
    public string? Model { get; init; }
}

/// <summary>An annotation in a drawing, as drw_annotation_list reads it (sheet mm).</summary>
public sealed class DrwAnnotationInfo
{
    /// <summary>The name to pass to the editing tools: a dimension's full name, else the annotation's name.</summary>
    public required string Name { get; init; }
    /// <summary>dimension | note | balloon | datum | gtol | surface_finish | center_mark | centerline | table | weld | …</summary>
    public required string Type { get; init; }
    /// <summary>The view it belongs to (the sheet's name for annotations on the sheet).</summary>
    public required string View { get; init; }
    /// <summary>The text as shown (links evaluated).</summary>
    public string? Text { get; init; }
    /// <summary>The text as stored, when it differs (property links like $PRPSHEET:"Description", formatting tags).</summary>
    public string? RawText { get; init; }
    public double[]? AtMm { get; init; }
    /// <summary>The approximate box [xMin, yMin, xMax, yMax] (text runs and lines; text widths are estimated).</summary>
    public double[]? BoxMm { get; init; }
    public double? HeightMm { get; init; }
    /// <summary>How many model entities it is attached to, and their kinds (edge, face, vertex, silhouette, …).</summary>
    public int Attached { get; init; }
    public IReadOnlyList<string>? AttachedTo { get; init; }
    /// <summary>Where attached edges and vertices are in the view's model (model mm; part views only).</summary>
    public IReadOnlyList<double[]>? AttachedAtModelMm { get; init; }
    /// <summary>The leader end points on the sheet.</summary>
    public IReadOnlyList<double[]>? LeadersMm { get; init; }
    /// <summary>Attached to geometry that no longer exists.</summary>
    public bool? Dangling { get; init; }
    /// <summary>Part of the sheet format (title block).</summary>
    public bool? InSheetFormat { get; init; }
    public DrwDimensionInfo? Dimension { get; init; }
    /// <summary>Tables: rows and columns (headers included) and the title.</summary>
    public int? Rows { get; init; }
    public int? Columns { get; init; }
}

public sealed class DrwAnnotationListResult : ToolResult
{
    public required IReadOnlyList<DrwAnnotationInfo> Annotations { get; init; }
    public int Total { get; init; }
    public bool? Truncated { get; init; }
    /// <summary>Annotations per type, over all that matched.</summary>
    public required IReadOnlyDictionary<string, int> Counts { get; init; }
}

/// <summary>drw_dimension_set: what to change on one display dimension; null leaves a setting as it is.</summary>
public sealed record DrwDimensionSetRequest
{
    public string? Doc { get; init; }
    public string? Name { get; init; }
    public string? Prefix { get; init; }
    public string? Suffix { get; init; }
    public string? Above { get; init; }
    public string? Below { get; init; }
    public DimTolerance? Tolerance { get; init; }
    public int? Precision { get; init; }
    public int? TolerancePrecision { get; init; }
    public double[]? TextAtMm { get; init; }
    public bool? Parentheses { get; init; }
    public double? HeightMm { get; init; }
}

/// <summary>drw_annotation_set: text, position and text height of a note, table or any other annotation.</summary>
public sealed record DrwAnnotationSetRequest
{
    public string? Doc { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public double[]? AtMm { get; init; }
    public double? HeightMm { get; init; }
    public double? AngleDeg { get; init; }
}

public sealed class DrwEditResult : ToolResult
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string View { get; init; }
    /// <summary>What changed, as "setting: old → new".</summary>
    public required IReadOnlyList<string> Changed { get; init; }
    /// <summary>The annotation as it reads now.</summary>
    public DrwAnnotationInfo? Now { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>One item drw_delete removed (or would remove).</summary>
public sealed class DrwDeleted
{
    public required string Name { get; init; }
    /// <summary>annotation type | view | detail_circle | section_line | sketch_segment.</summary>
    public required string Kind { get; init; }
    public string? View { get; init; }
}

public sealed class DrwDeleteResult : ToolResult
{
    public required IReadOnlyList<DrwDeleted> Deleted { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed partial class DrawingService
{
    private const int MaxAnnotations = 300;

    /// <summary>An annotation found on the current sheet, with the view it belongs to.</summary>
    private sealed record AnnHit(IAnnotation Ann, string Name, string Type, string View, ViewHit Owner, object? Specific)
    {
        public IDisplayDimension? Dim => Specific as IDisplayDimension;
        public INote? Note => Specific as INote;
        public ITableAnnotation? Table => Specific as ITableAnnotation;
    }

    // ================================================================== enumeration

    /// <summary>
    /// Every annotation of the current sheet: the sheet's own (notes, tables, title block) and each view's, from views found
    /// by traversal (methods on ISheet.GetViews objects did nothing live, see AllViews).
    /// </summary>
    private static List<AnnHit> SheetAnnotations(IDrawingDoc drw, IReadOnlyCollection<string>? views = null)
    {
        string? cur = drw.DrwCurrentSheet()?.GetName();
        var list = new List<AnnHit>();
        foreach (var vh in AllViews(drw).Where(h => string.Equals(h.Sheet, cur, StringComparison.OrdinalIgnoreCase)))
        {
            if (views is { Count: > 0 } && !views.Contains(vh.Name, StringComparer.OrdinalIgnoreCase)) continue;
            List<IAnnotation> anns;
            try { anns = vh.View.DrwAnnotations(); }
            catch (COMException e) when (!ComArgs.IsDisconnect(e)) { continue; }
            foreach (var an in anns)
            {
                object? spec = Safe(() => (object?)an.GetSpecificAnnotation(), null);
                string type = AnnType(Safe(() => an.GetType(), 0), spec);
                string name = spec is IDisplayDimension dd ? Safe(() => dd.GetDimension2(0)?.FullName, null) ?? Safe(() => an.GetName(), "") ?? ""
                                                           : Safe(() => an.GetName(), "") ?? "";
                list.Add(new AnnHit(an, name, type, vh.Name, vh, spec));
            }
        }
        return list;
    }

    private static string AnnType(int t, object? spec) => t switch
    {
        (int)swAnnotationType_e.swDisplayDimension => "dimension",
        (int)swAnnotationType_e.swNote => spec is INote n && Safe(() => n.IsBomBalloon() || n.HasBalloon(), false) ? "balloon" : "note",
        (int)swAnnotationType_e.swDatumTag => "datum",
        (int)swAnnotationType_e.swGTol => "gtol",
        (int)swAnnotationType_e.swSFSymbol => "surface_finish",
        (int)swAnnotationType_e.swCenterMarkSym => "center_mark",
        (int)swAnnotationType_e.swCenterLine => "centerline",
        (int)swAnnotationType_e.swTableAnnotation => "table",
        (int)swAnnotationType_e.swWeldSymbol or (int)swAnnotationType_e.swWeldBeadSymbol => "weld",
        (int)swAnnotationType_e.swDatumTargetSym => "datum_target",
        (int)swAnnotationType_e.swCThread => "cosmetic_thread",
        (int)swAnnotationType_e.swBlock => "block",
        (int)swAnnotationType_e.swCustomSymbol => "symbol",
        (int)swAnnotationType_e.swDatumOrigin => "datum_origin",
        (int)swAnnotationType_e.swRevisionCloud => "revision_cloud",
        _ => $"type {t}",
    };

    /// <summary>One annotation by name (see <see cref="DrawingEdit.NameMatches"/>); ambiguous names list the candidates.</summary>
    private static AnnHit FindAnnotation(IDrawingDoc drw, string? name, string arg = "name")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw SwException.InvalidArgument($"'{arg}' must name an annotation (drw_annotation_list lists them), e.g. \"RD1@Drawing View1\" or \"DetailItem3\".");
        var all = SheetAnnotations(drw);
        var hits = all.Where(a => DrawingEdit.NameMatches(name, Safe(() => a.Ann.GetName(), "") ?? "", a.Dim is null ? null : a.Name, a.View)).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1)
            throw new SwException(ErrorCodes.EntityAmbiguous, $"'{name}' names {hits.Count} annotations on this sheet.",
                $"Qualify it with the view: {string.Join(", ", hits.Take(6).Select(h => $"{DrawingEdit.ShortDim(h.Name)}@{h.View}"))}.");
        throw new SwException(ErrorCodes.EntityNotFound, $"No annotation named '{name}' on the current sheet.",
            all.Count == 0 ? "The sheet has no annotations." : $"Some names: {string.Join(", ", all.Take(8).Select(a => a.Name))}{(all.Count > 8 ? ", …" : "")} (drw_annotation_list lists them all).");
    }

    // ================================================================== drw_annotation_list

    public Task<DrwAnnotationListResult> AnnotationListAsync(IReadOnlyList<string>? views, IReadOnlyList<string>? types, int limit, int offset,
        string? doc, CancellationToken ct = default)
    {
        if (limit is < 1 or > MaxAnnotations) throw SwException.InvalidArgument($"limit must be 1..{MaxAnnotations} (got {limit}).");
        if (offset < 0) throw SwException.InvalidArgument("offset must be ≥ 0.");
        var typeSet = types is { Count: > 0 } ? types.Select(t => DrawingEdit.Norm(t) ?? "").ToHashSet() : null;
        return session.RunAsync("drw_annotation_list", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            if (views is { Count: > 0 })
                foreach (var (v, i) in views.Select((v, i) => (v, i)))
                    if (!AllViews(drw).Any(h => string.Equals(h.Name, v, StringComparison.OrdinalIgnoreCase)))
                        FindView(ctx, drw, v, $"views[{i}]"); // throws with the list of views
            var all = SheetAnnotations(drw, views).Where(a => typeSet is null || typeSet.Contains(a.Type)).ToList();
            var page = all.Skip(offset).Take(limit).Select(a => Describe(ctx, a)).ToList();
            return new DrwAnnotationListResult
            {
                Doc = ctx.Title(d),
                Annotations = page,
                Total = all.Count,
                Truncated = offset + page.Count < all.Count ? true : null,
                Counts = all.GroupBy(a => a.Type).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            };
        }, RunOptions.Read(doc), ct);
    }

    /// <summary>Everything drw_annotation_list reports about one annotation.</summary>
    private static DrwAnnotationInfo Describe(SwContext ctx, AnnHit a)
    {
        var an = a.Ann;
        var pos = Safe(() => an.DrwPosition(), []);
        int attached = Safe(() => an.GetAttachedEntityCount3(), 0);
        var types = attached > 0 ? ComArgs.Ints(Safe(() => (object?)an.GetAttachedEntityTypes(), null)).Select(SelName).ToList() : [];
        var at = new List<double[]>();
        if (attached > 0)
        {
            foreach (var o in ComArgs.Objects<object>(Safe(() => (object?)an.GetAttachedEntities3(), null)).Take(6))
            {
                try
                {
                    switch (o)
                    {
                        case IVertex vx: at.Add(Units.Mm(Vec3.From(vx.Point()))); break;
                        case IEdge e: at.Add(Units.Mm(Topology.Describe(e).Mid)); break;
                    }
                }
                catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* not a model entity */ }
            }
        }
        var leaders = new List<double[]>();
        int lc = Safe(() => an.GetLeaderCount(), 0);
        for (int i = 0; i < Math.Min(lc, 6); i++)
        {
            var pts = ComArgs.Doubles(Safe(() => (object?)an.GetLeaderPointsAtIndex(i), null));
            if (pts.Length >= 3) leaders.Add([Math.Round(pts[0] * 1000, 3), Math.Round(pts[1] * 1000, 3)]);
        }
        string? text = null, raw = null;
        double? height = null;
        int? rows = null, cols = null;
        DrwDimensionInfo? dim = null;
        switch (a.Specific)
        {
            case INote n:
                text = DrawingEdit.Plain(Safe(() => n.GetText(), null));
                raw = DrawingEdit.Plain(Safe(() => n.PropertyLinkedText, null));
                height = Safe(() => (double?)Units.Mm(n.GetHeight()), null);
                break;
            case IDisplayDimension dd:
                dim = DimInfo(ctx, dd);
                text = dim is null ? null : $"{dim.Prefix}{dim.Value}{dim.Suffix}";
                break;
            case ITableAnnotation t:
                rows = Safe(() => (int?)t.RowCount, null);
                cols = Safe(() => (int?)t.ColumnCount, null);
                text = Safe(() => t.Title, null);
                break;
            case IDatumTag dt:
                text = Safe(() => dt.GetLabel(), null);
                break;
        }
        var box = Box(a);
        return new DrwAnnotationInfo
        {
            Name = a.Name,
            Type = a.Type,
            View = a.View,
            Text = string.IsNullOrEmpty(text) ? null : text,
            RawText = raw is { Length: > 0 } && raw != text ? raw : null,
            AtMm = pos.Length >= 2 ? [Math.Round(pos[0] * 1000, 3), Math.Round(pos[1] * 1000, 3)] : null,
            BoxMm = box?.ToArray(),
            HeightMm = height,
            Attached = attached,
            AttachedTo = types.Count > 0 ? types : null,
            AttachedAtModelMm = at.Count > 0 ? at : null,
            LeadersMm = leaders.Count > 0 ? leaders : null,
            Dangling = Safe(() => an.IsDangling(), false) ? true : null,
            InSheetFormat = InFormat(a) ? true : null,
            Dimension = dim,
            Rows = rows,
            Columns = cols,
        };
    }

    /// <summary>A note of the sheet (not a view) whose layer or owner says it belongs to the sheet format: best effort.</summary>
    private static bool InFormat(AnnHit a)
    {
        if (a.Owner.View.Type != (int)swDrawingViewTypes_e.swDrawingSheet) return false;
        try { return a.Ann.OwnerType == (int)swAnnotationOwner_e.swAnnotationOwner_DrawingTemplate; }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { return false; }
    }

    private static string SelName(int t) => t switch
    {
        (int)swSelectType_e.swSelEDGES => "edge",
        (int)swSelectType_e.swSelFACES => "face",
        (int)swSelectType_e.swSelVERTICES => "vertex",
        (int)swSelectType_e.swSelSILHOUETTES => "silhouette",
        (int)swSelectType_e.swSelSKETCHSEGS => "sketch_segment",
        (int)swSelectType_e.swSelSKETCHPOINTS => "sketch_point",
        (int)swSelectType_e.swSelDRAWINGVIEWS => "view",
        (int)swSelectType_e.swSelCENTERMARKSYMS => "center_mark",
        (int)swSelectType_e.swSelCENTERLINES => "centerline",
        (int)swSelectType_e.swSelNOTES => "note",
        _ => $"type {t}",
    };

    private static string DimKind(int t) => t switch
    {
        (int)swDimensionType_e.swLinearDimension => "linear",
        (int)swDimensionType_e.swHorLinearDimension => "horizontal",
        (int)swDimensionType_e.swVertLinearDimension => "vertical",
        (int)swDimensionType_e.swAngularDimension => "angular",
        (int)swDimensionType_e.swRadialDimension => "radial",
        (int)swDimensionType_e.swDiameterDimension => "diameter",
        (int)swDimensionType_e.swChamferDimension => "chamfer",
        (int)swDimensionType_e.swOrdinateDimension => "ordinate",
        (int)swDimensionType_e.swHorOrdinateDimension => "horizontal_ordinate",
        (int)swDimensionType_e.swVertOrdinateDimension => "vertical_ordinate",
        (int)swDimensionType_e.swArcLengthDimension => "arc_length",
        (int)swDimensionType_e.swRadialLinearDimension => "radial_linear",
        (int)swDimensionType_e.swDiametricLinearDimension => "diametric_linear",
        (int)swDimensionType_e.swAngularOrdinateDimension => "angular_ordinate",
        _ => $"type {t}",
    };

    private static DrwDimensionInfo? DimInfo(SwContext ctx, IDisplayDimension dd)
    {
        var dim = Safe(() => dd.GetDimension2(0), null);
        if (dim is null) return null;
        bool angular = Safe(() => dim.GetType(), 0) == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
        double Conv(double v) => angular ? Units.Deg(v) : Units.Mm(v);
        var tol = Safe(() => dim.Tolerance, null);
        int tolType = tol is null ? 0 : Safe(() => tol.Type, 0);
        double? plus = null, minus = null;
        string? hole = null, shaft = null;
        if (tol is not null && tolType is 2 or 3 or 4 or 5 or 6)
        {
            double mx = 0, mn = 0;
            if (Safe(() => tol.GetMaxValue2(out mx), -1) >= 0) plus = Conv(mx);
            if (Safe(() => tol.GetMinValue2(out mn), -1) >= 0) minus = Conv(-mn);
        }
        if (tol is not null && tolType is 7 or 8 or 9)
        {
            hole = Safe(() => tol.GetHoleFitValue(), null) is { Length: > 0 } h ? h : null;
            shaft = Safe(() => tol.GetShaftFitValue(), null) is { Length: > 0 } s ? s : null;
        }
        string full = Safe(() => dim.FullName, "") ?? "";
        bool driven = Safe(() => dim.DrivenState, 0) == (int)swDimensionDrivenState_e.swDimensionDriven;
        string? S(int part) => Safe(() => dd.GetText(part), null) is { Length: > 0 } x ? x : null;
        return new DrwDimensionInfo
        {
            Kind = DimKind(Safe(() => dd.GetType(), 0)),
            FullName = full,
            Value = Conv(Safe(() => dim.SystemValue, double.NaN)),
            Unit = angular ? "deg" : "mm",
            Prefix = S((int)swDimensionTextParts_e.swDimensionTextPrefix),
            Suffix = S((int)swDimensionTextParts_e.swDimensionTextSuffix),
            Above = S((int)swDimensionTextParts_e.swDimensionTextCalloutAbove),
            Below = S((int)swDimensionTextParts_e.swDimensionTextCalloutBelow),
            Tolerance = DrawingEdit.TolName(tolType),
            PlusMm = plus,
            MinusMm = minus,
            Hole = hole,
            Shaft = shaft,
            Precision = Safe(() => dd.GetUseDocPrecision(), false) ? -1 : Safe(() => (int?)dd.GetPrimaryPrecision2(), null),
            TolerancePrecision = Safe(() => (int?)dd.GetPrimaryTolPrecision2(), null),
            Parentheses = Safe(() => dd.ShowParenthesis, false) ? true : null,
            Reference = driven ? true : null,
            Model = driven || !full.Contains('@') ? null : full.Split('@').Last(),
        };
    }

    /// <summary>The approximate sheet box of an annotation (mm): notes by their extent, dimensions by their display data, tables by size.</summary>
    private static SheetRect? Box(AnnHit a)
    {
        try
        {
            switch (a.Specific)
            {
                case INote n:
                {
                    var e = ComArgs.Doubles((object?)n.GetExtent());
                    if (e.Length >= 6) return DrawingEdit.Extent([(e[0], e[1]), (e[3], e[4])]);
                    break;
                }
                case ITableAnnotation t:
                {
                    var p = a.Ann.DrwPosition();
                    if (p.Length < 2) return null;
                    double w = 0, h = 0;
                    for (int c = 0; c < t.ColumnCount; c++) w += t.GetColumnWidth(c);
                    for (int r = 0; r < t.RowCount; r++) h += t.GetRowHeight(r);
                    // The table's position is its top-left corner (the anchor of drw_bom_add is top-right; SOLIDWORKS reports top-left).
                    return DrawingEdit.Extent([(p[0], p[1] - h), (p[0] + w, p[1])]);
                }
            }
            if ((object?)a.Ann.GetDisplayData() is IDisplayData dd) return DisplayBox(dd);
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* no box */ }
        return null;
    }

    /// <summary>The box of an annotation's display data: its lines, arcs and text runs (text width estimated).</summary>
    private static SheetRect? DisplayBox(IDisplayData dd)
    {
        var pts = new List<(double, double)>();
        int lines = Safe(() => dd.GetLineCount(), 0);
        for (int i = 0; i < Math.Min(lines, 200); i++)
        {
            var l = ComArgs.Doubles(Safe(() => (object?)dd.GetLineAtIndex(i), null));
            if (l.Length >= 6) { pts.Add((l[0], l[1])); pts.Add((l[3], l[4])); }
        }
        int arcs = Safe(() => dd.GetArcCount(), 0);
        for (int i = 0; i < Math.Min(arcs, 100); i++)
        {
            var c = ComArgs.Doubles(Safe(() => (object?)dd.GetArcAtIndex(i), null));
            if (c.Length >= 9) { pts.Add((c[3], c[4])); pts.Add((c[6], c[7])); }
        }
        SheetRect? box = DrawingEdit.Extent(pts);
        int texts = Safe(() => dd.GetTextCount(), 0);
        for (int i = 0; i < Math.Min(texts, 50); i++)
        {
            var p = ComArgs.Doubles(Safe(() => (object?)dd.GetTextPositionAtIndex(i), null));
            if (p.Length < 2) continue;
            double h = Safe(() => dd.GetTextHeightAtIndex(i), 0.0035) * 1000;
            string s = Safe(() => dd.GetTextAtIndex(i), "") ?? "";
            var tb = DrawingEdit.TextBox(p[0] * 1000, p[1] * 1000, h, s.Length, Safe(() => dd.GetTextAngleAtIndex(i), 0.0));
            box = box is SheetRect b ? DrawingEdit.Union(b, tb) : tb;
        }
        return box;
    }

    // ================================================================== drw_dimension_set

    public Task<DrwEditResult> DimensionSetAsync(DrwDimensionSetRequest q, CancellationToken ct = default)
    {
        ValidateDimSet(q);
        return session.RunAsync("drw_dimension_set", ctx => DimensionSet(ctx, q), RunOptions.Write(q.Doc), ct);
    }

    private static void ValidateDimSet(DrwDimensionSetRequest q)
    {
        if (q.Prefix is null && q.Suffix is null && q.Above is null && q.Below is null && q.Tolerance is null && q.Precision is null &&
            q.TolerancePrecision is null && q.TextAtMm is null && q.Parentheses is null && q.HeightMm is null)
            throw SwException.InvalidArgument("Nothing to change.", "Pass prefix, suffix, above, below, tolerance, precision, tolerancePrecision, textAtMm, parentheses and/or heightMm.");
        DrawingEdit.Precision(q.Precision, "precision");
        DrawingEdit.Precision(q.TolerancePrecision, "tolerancePrecision");
        if (q.TextAtMm is not null) Sheet2(q.TextAtMm, "textAtMm");
        if (q.HeightMm is double h && (!double.IsFinite(h) || h < 0.5 || h > 100)) throw SwException.InvalidArgument($"heightMm must be 0.5..100 (got {h}).");
        foreach (var (n, v) in new[] { ("prefix", q.Prefix), ("suffix", q.Suffix), ("above", q.Above), ("below", q.Below) })
            if (v is { Length: > 200 }) throw SwException.InvalidArgument($"'{n}' is longer than 200 characters.");
    }

    /// <summary>
    /// Applies each requested setting and reads it back; when one doesn't take, everything changed so far is put back and
    /// the call fails (VERIFY_FAILED). A tolerance on a model dimension changes the model, so that model must be the
    /// server's (owned or adopted).
    /// </summary>
    private static DrwEditResult DimensionSet(SwContext ctx, DrwDimensionSetRequest q)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var a = FindAnnotation(drw, q.Name);
        var dd = a.Dim ?? throw SwException.InvalidArgument($"'{a.Name}' is a {a.Type}, not a dimension.", "Use drw_annotation_set for notes and other annotations.");
        var dim = dd.GetDimension2(0) ?? throw new SwException(ErrorCodes.Internal, $"'{a.Name}' has no dimension value.");
        bool angular = dim.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
        bool driven = dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven;
        var tolPlan = q.Tolerance is null ? null : DrawingEdit.Tolerance(q.Tolerance, angular);
        if (tolPlan is not null && !driven) RequireOwnedModel(ctx, a);

        var before = DimInfo(ctx, dd)!;
        var changed = new List<string>();
        var undo = new List<Action>();
        var problems = new List<string>();
        var checks = new List<string>();
        void Text(int part, int defPart, string? want, string label, string? old)
        {
            if (want is null) return;
            dd.SetText(part, want);
            undo.Add(() => dd.SetText(part, old ?? ""));
            string got = dd.GetText(defPart) is { Length: > 0 } def ? def : dd.GetText(part) ?? "";
            if (!SameDimText(got, want)) problems.Add($"{label} reads '{got}', not '{want}'");
            else { changed.Add($"{label}: '{old ?? ""}' → '{want}'"); checks.Add($"{label} read back"); }
        }
        try
        {
            Text((int)swDimensionTextParts_e.swDimensionTextPrefix, (int)swDimensionTextParts_e.swDimensionTextPrefixDefinition, q.Prefix, "prefix", before.Prefix);
            Text((int)swDimensionTextParts_e.swDimensionTextSuffix, (int)swDimensionTextParts_e.swDimensionTextSuffixDefinition, q.Suffix, "suffix", before.Suffix);
            Text((int)swDimensionTextParts_e.swDimensionTextCalloutAbove, (int)swDimensionTextParts_e.swDimensionTextCalloutAboveDefinition, q.Above, "above", before.Above);
            Text((int)swDimensionTextParts_e.swDimensionTextCalloutBelow, (int)swDimensionTextParts_e.swDimensionTextCalloutBelowDefinition, q.Below, "below", before.Below);

            if (tolPlan is not null)
            {
                var tol = dim.Tolerance ?? throw new SwException(ErrorCodes.NotSupported, $"'{a.Name}' takes no tolerance.");
                int oldType = tol.Type;
                double oldMin = 0, oldMax = 0;
                tol.GetMinValue2(out oldMin);
                tol.GetMaxValue2(out oldMax);
                undo.Add(() =>
                {
                    tol.Type = oldType;
                    if (oldType is 2 or 3 or 4 or 5 or 6) tol.SetValues2(oldMin, oldMax, (int)swSetValueInConfiguration_e.swSetValue_UseCurrentSetting, ComArgs.Empty);
                });
                tol.Type = tolPlan.Code;
                double M(double v) => angular ? Units.Rad(v) : Units.M(v);
                if (tolPlan.HasValues)
                    tol.SetValues2(M(tolPlan.MinMm), M(tolPlan.MaxMm), (int)swSetValueInConfiguration_e.swSetValue_UseCurrentSetting, ComArgs.Empty);
                if (tolPlan.Name == "fit") tol.SetFitValues(tolPlan.Hole ?? "", tolPlan.Shaft ?? "");
                int gotType = tol.Type;
                var nowInfo = DimInfo(ctx, dd)!;
                if (gotType != tolPlan.Code) problems.Add($"tolerance type reads {DrawingEdit.TolName(gotType)}, not {tolPlan.Name}");
                else if (tolPlan.HasValues && (Diff(nowInfo.PlusMm, tolPlan.MaxMm) || Diff(nowInfo.MinusMm, -tolPlan.MinMm)))
                    problems.Add($"tolerance reads +{nowInfo.PlusMm}/−{nowInfo.MinusMm}, not +{tolPlan.MaxMm}/−{-tolPlan.MinMm}");
                else if (tolPlan.Name == "fit" && (tolPlan.Hole is not null && nowInfo.Hole != tolPlan.Hole || tolPlan.Shaft is not null && nowInfo.Shaft != tolPlan.Shaft))
                    problems.Add($"fit reads {nowInfo.Hole}/{nowInfo.Shaft}, not {tolPlan.Hole}/{tolPlan.Shaft}");
                else
                {
                    changed.Add($"tolerance: {before.Tolerance}{Dev(before)} → {tolPlan.Name}{Dev(nowInfo)}");
                    checks.Add("tolerance type and values read back");
                    if (!driven) ctx.Warn($"'{a.Name}' is a model dimension: its tolerance changed in '{before.Model}' too (save that model to keep it).");
                }
            }

            if (q.Precision is not null || q.TolerancePrecision is not null)
            {
                int oldP = dd.GetPrimaryPrecision2(), oldT = dd.GetPrimaryTolPrecision2();
                bool oldDoc = dd.GetUseDocPrecision();
                undo.Add(() => dd.SetPrecision3(oldDoc ? -2 : oldP, -1, oldT, -1));
                int P(int? v) => v is null ? (int)swDimensionPrecisionSettings_e.swDoNotChangePrecisionSetting
                    : v == -1 ? (int)swDimensionPrecisionSettings_e.swPrecisionFollowsDocumentSetting : v.Value;
                dd.SetPrecision3(P(q.Precision), (int)swDimensionPrecisionSettings_e.swDoNotChangePrecisionSetting, P(q.TolerancePrecision),
                    (int)swDimensionPrecisionSettings_e.swDoNotChangePrecisionSetting);
                if (q.Precision is int p && p >= 0 && dd.GetPrimaryPrecision2() != p) problems.Add($"precision reads {dd.GetPrimaryPrecision2()}, not {p}");
                else if (q.TolerancePrecision is int tp && tp >= 0 && dd.GetPrimaryTolPrecision2() != tp) problems.Add($"tolerance precision reads {dd.GetPrimaryTolPrecision2()}, not {tp}");
                else
                {
                    if (q.Precision is not null) changed.Add($"precision: {before.Precision} → {q.Precision}");
                    if (q.TolerancePrecision is not null) changed.Add($"tolerancePrecision: {before.TolerancePrecision} → {q.TolerancePrecision}");
                    checks.Add("precision read back");
                }
            }

            if (q.Parentheses is bool par)
            {
                bool old = dd.ShowParenthesis;
                undo.Add(() => dd.ShowParenthesis = old);
                dd.ShowParenthesis = par;
                if (dd.ShowParenthesis != par) problems.Add($"parentheses read {dd.ShowParenthesis}");
                else changed.Add($"parentheses: {old} → {par}");
            }

            if (q.HeightMm is double hm) SetHeight(a, Units.M(hm), changed, problems, undo);
            if (q.TextAtMm is not null) Move(ctx, d, a, Sheet2(q.TextAtMm, "textAtMm"), changed, problems, undo, checks);

            if (problems.Count > 0)
            {
                string back = Revert(undo);
                throw new SwException(ErrorCodes.VerifyFailed, $"'{a.Name}': {string.Join("; ", problems)}. {back}",
                    "Check the values (drw_annotation_list shows the dimension as it is).");
            }
        }
        catch (Exception ex) when (ex is COMException && !ComArgs.IsDisconnect(ex))
        {
            string back = Revert(undo);
            throw new SwException(ErrorCodes.FeatureFailed, $"'{a.Name}': {ex.Message}. {back}", null, ex);
        }
        d.GraphicsRedraw2();
        return new DrwEditResult { Doc = ctx.Title(d), Name = a.Name, Type = a.Type, View = a.View, Changed = changed, Now = Describe(ctx, a), Checks = checks };
    }

    private static string Dev(DrwDimensionInfo i) => i.PlusMm is null && i.MinusMm is null ? i.Hole is null && i.Shaft is null ? "" : $" {i.Hole}{i.Shaft}"
        : $" +{i.PlusMm}/−{i.MinusMm}";

    private static bool Diff(double? got, double want) => got is not double g || Math.Abs(g - want) > 1e-6;

    /// <summary>Dimension text compared as SOLIDWORKS stores it (Ø may come back as &lt;MOD-DIAM&gt;).</summary>
    private static bool SameDimText(string got, string want)
    {
        static string N(string s) => s.Replace("<MOD-DIAM>", "Ø", StringComparison.OrdinalIgnoreCase).Replace("<MOD-PM>", "±", StringComparison.OrdinalIgnoreCase)
            .Replace("<MOD-DEG>", "°", StringComparison.OrdinalIgnoreCase).Replace("\r\n", "\n").Trim();
        return N(got) == N(want);
    }

    /// <summary>NOT_OWNED unless the model that owns a model dimension is a document this server may change.</summary>
    private static void RequireOwnedModel(SwContext ctx, AnnHit a)
    {
        var model = a.Owner.View.ReferencedDocument;
        if (model is null) return;
        var e = ctx.Documents.Track(model);
        if (e.Class is DocClass.Owned or DocClass.Adopted) return;
        throw new SwException(ErrorCodes.NotOwned,
            $"'{a.Name}' is a dimension of '{e.Title}', which this server didn't create or open: its tolerance would change that model.",
            $"doc_activate {{doc:'{e.Title}', allowEdit:true}} first (a backup is made), or tolerance a reference dimension of the drawing.");
    }

    private static string Revert(List<Action> undo)
    {
        int failed = 0;
        for (int i = undo.Count - 1; i >= 0; i--)
        {
            try { undo[i](); }
            catch (COMException e) when (!ComArgs.IsDisconnect(e)) { failed++; }
        }
        return undo.Count == 0 ? "Nothing was changed." : failed == 0 ? "The changes made in this call were put back." : $"{failed} change(s) could not be put back.";
    }

    private static void SetHeight(AnnHit a, double h, List<string> changed, List<string> problems, List<Action> undo)
    {
        if (a.Note is INote n)
        {
            double old = n.GetHeight();
            undo.Add(() => n.SetHeight(old));
            n.SetHeight(h);
            if (Math.Abs(n.GetHeight() - h) > 1e-7 && (object?)a.Ann.GetTextFormat(0) is ITextFormat tf0)
            {
                tf0.CharHeight = h;
                a.Ann.SetTextFormat(0, false, tf0);
            }
            if (Math.Abs(n.GetHeight() - h) > 1e-7) problems.Add($"text height reads {Units.Mm(n.GetHeight())} mm");
            else changed.Add($"heightMm: {Units.Mm(old)} → {Units.Mm(h)}");
            return;
        }
        if (a.Table is ITableAnnotation t)
        {
            var tf = t.GetTextFormat();
            double old = tf.CharHeight;
            bool oldDoc = t.GetUseDocTextFormat();
            undo.Add(() => { var b = t.GetTextFormat(); b.CharHeight = old; t.SetTextFormat(oldDoc, b); });
            tf.CharHeight = h;
            t.SetTextFormat(false, tf);
            double got = t.GetTextFormat().CharHeight;
            if (Math.Abs(got - h) > 1e-7) problems.Add($"table text height reads {Units.Mm(got)} mm");
            else changed.Add($"heightMm: {Units.Mm(old)} → {Units.Mm(h)}");
            return;
        }
        if ((object?)a.Ann.GetTextFormat(0) is not ITextFormat f)
        {
            problems.Add($"a {a.Type} has no text format to change");
            return;
        }
        double prev = f.CharHeight;
        bool prevDoc = a.Ann.GetUseDocTextFormat(0);
        undo.Add(() => { if ((object?)a.Ann.GetTextFormat(0) is ITextFormat b) { b.CharHeight = prev; a.Ann.SetTextFormat(0, prevDoc, b); } });
        f.CharHeight = h;
        a.Ann.SetTextFormat(0, false, f);
        double now = (object?)a.Ann.GetTextFormat(0) is ITextFormat g ? g.CharHeight : double.NaN;
        if (Math.Abs(now - h) > 1e-7) problems.Add($"text height reads {Units.Mm(now)} mm");
        else changed.Add($"heightMm: {Units.Mm(prev)} → {Units.Mm(h)}");
    }

    private static void Move(SwContext ctx, IModelDoc2 d, AnnHit a, (double X, double Y) to, List<string> changed, List<string> problems,
        List<Action> undo, List<string> checks)
    {
        var old = a.Ann.DrwPosition();
        if (old.Length >= 2) undo.Add(() => a.Ann.SetPosition2(old[0], old[1], old.Length > 2 ? old[2] : 0));
        WarnOffSheet(ctx, d, to, a.Name);
        bool ok = a.Ann.SetPosition2(Units.M(to.X), Units.M(to.Y), 0);
        var now = a.Ann.DrwPosition();
        if (now.Length >= 2 && Math.Abs(now[0] - Units.M(to.X)) <= 1e-4 && Math.Abs(now[1] - Units.M(to.Y)) <= 1e-4)
        {
            changed.Add($"atMm: [{(old.Length >= 2 ? Fmt([old[0] * 1000, old[1] * 1000]) : "?")}] → [{Fmt([to.X, to.Y])}]");
            checks.Add("position read back");
        }
        else problems.Add($"the position reads [{(now.Length >= 2 ? Fmt([now[0] * 1000, now[1] * 1000]) : "?")}] mm{(ok ? "" : " (SetPosition2 returned false)")}");
    }

    /// <summary>Warns when a sheet position lies outside the drawing frame (the sheet inside its border).</summary>
    private static void WarnOffSheet(SwContext ctx, IModelDoc2 d, (double X, double Y) p, string what)
    {
        try
        {
            var sheet = ctx.Drawing(d).DrwCurrentSheet();
            if (sheet is null) return;
            var frame = Frame(sheet);
            if (!frame.Contains(p.X, p.Y))
                ctx.Warn($"'{what}' goes to [{Fmt([p.X, p.Y])}] mm, outside the drawing frame [{Fmt(frame.ToArray())}] mm.");
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* informational */ }
    }

    /// <summary>The drawing frame: the area inside the sheet format's border (DrawingLayout.Usable), else the sheet less 10 mm.</summary>
    private static SheetRect Frame(ISheet sheet)
    {
        var sp = sheet.DrwProps();
        if (sp.Length < 7) return new SheetRect(-1e5, -1e5, 1e5, 1e5);
        bool hasFormat = (int)Math.Round(sp[1]) != TemplateNone && !string.IsNullOrEmpty(sheet.GetTemplateName());
        var u = DrawingLayout.Usable(sp[5] * 1000, sp[6] * 1000, hasFormat);
        return hasFormat ? new SheetRect(u.X0, Math.Max(10, u.Y0 - 60), u.X1, u.Y1) : new SheetRect(u.X0, u.Y0, u.X1, u.Y1);
    }

    // ================================================================== drw_annotation_set

    public Task<DrwEditResult> AnnotationSetAsync(DrwAnnotationSetRequest q, CancellationToken ct = default)
    {
        ValidateAnnSet(q);
        return session.RunAsync("drw_annotation_set", ctx => AnnotationSet(ctx, q), RunOptions.Write(q.Doc), ct);
    }

    private static void ValidateAnnSet(DrwAnnotationSetRequest q)
    {
        if (q.Text is null && q.AtMm is null && q.HeightMm is null && q.AngleDeg is null)
            throw SwException.InvalidArgument("Nothing to change.", "Pass text, atMm, heightMm and/or angleDeg.");
        if (q.Text is { Length: > 4000 }) throw SwException.InvalidArgument($"'text' is {q.Text.Length} characters long (at most 4000).");
        if (q.Text is { Length: 0 }) throw SwException.InvalidArgument("'text' is empty; delete the note with drw_delete instead.");
        if (q.AtMm is not null) Sheet2(q.AtMm, "atMm");
        if (q.HeightMm is double h && (!double.IsFinite(h) || h < 0.5 || h > 500)) throw SwException.InvalidArgument($"heightMm must be 0.5..500 (got {h}).");
        if (q.AngleDeg is double ang && (!double.IsFinite(ang) || Math.Abs(ang) > 360)) throw SwException.InvalidArgument($"angleDeg must be -360..360 (got {ang}).");
    }

    private static DrwEditResult AnnotationSet(SwContext ctx, DrwAnnotationSetRequest q)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var a = FindAnnotation(drw, q.Name);
        if (q.Text is not null && a.Note is null)
            throw SwException.InvalidArgument($"'{a.Name}' is a {a.Type}; only notes (and balloons) take new text.",
                a.Type == "dimension" ? "Use drw_dimension_set for prefix, suffix and tolerance." : a.Type == "table" ? "Use drw_table_set for cells." : null);
        if (q.AngleDeg is not null && a.Note is null) throw SwException.InvalidArgument($"angleDeg applies to notes only ('{a.Name}' is a {a.Type}).");
        var changed = new List<string>();
        var problems = new List<string>();
        var undo = new List<Action>();
        var checks = new List<string>();
        try
        {
            if (q.Text is not null)
            {
                var n = a.Note!;
                string oldRaw = n.PropertyLinkedText is { Length: > 0 } r ? r : n.GetText() ?? "";
                string api = DrawingEdit.NoteText(q.Text);
                undo.Add(() => n.SetText(oldRaw));
                bool ok = n.SetText(api);
                string raw = DrawingEdit.Plain(n.PropertyLinkedText is { Length: > 0 } r2 ? r2 : n.GetText());
                string want = DrawingEdit.Plain(api);
                if (raw != want && !(DrawingEdit.HasLinks(want) && DrawingEdit.Plain(n.GetText()).Length > 0))
                    problems.Add($"the text reads '{Clip(raw, 80)}'{(ok ? "" : " (SetText returned false)")}");
                else
                {
                    changed.Add($"text: '{Clip(DrawingEdit.Plain(oldRaw), 60)}' → '{Clip(want, 60)}'");
                    checks.Add(DrawingEdit.HasLinks(want) ? $"text set; it shows '{Clip(DrawingEdit.Plain(n.GetText()), 60)}'" : "text read back");
                }
            }
            if (q.AngleDeg is double ang)
            {
                var n = a.Note!;
                double old = n.Angle;
                undo.Add(() => n.Angle = old);
                n.Angle = Units.Rad(ang);
                if (Math.Abs(n.Angle - Units.Rad(ang)) > 1e-6) problems.Add($"the angle reads {Units.Deg(n.Angle)}°");
                else changed.Add($"angleDeg: {Units.Deg(old)} → {ang}");
            }
            if (q.HeightMm is double hm) SetHeight(a, Units.M(hm), changed, problems, undo);
            if (q.AtMm is not null) Move(ctx, d, a, Sheet2(q.AtMm, "atMm"), changed, problems, undo, checks);
            if (problems.Count > 0)
            {
                string back = Revert(undo);
                throw new SwException(ErrorCodes.VerifyFailed, $"'{a.Name}': {string.Join("; ", problems)}. {back}");
            }
        }
        catch (Exception ex) when (ex is COMException && !ComArgs.IsDisconnect(ex))
        {
            string back = Revert(undo);
            throw new SwException(ErrorCodes.FeatureFailed, $"'{a.Name}': {ex.Message}. {back}", null, ex);
        }
        d.GraphicsRedraw2();
        return new DrwEditResult { Doc = ctx.Title(d), Name = a.Name, Type = a.Type, View = a.View, Changed = changed, Now = Describe(ctx, a), Checks = checks };
    }

    // ================================================================== drw_delete

    public Task<DrwDeleteResult> DeleteAsync(IReadOnlyList<string>? names, bool dryRun, string? doc, CancellationToken ct = default)
    {
        if (names is null || names.Count == 0) throw SwException.InvalidArgument("'names' is empty: pass what to delete (drw_annotation_list and drw_info list names).");
        if (names.Count > 100) throw SwException.InvalidArgument($"At most 100 names per call (got {names.Count}).");
        if (names.Any(string.IsNullOrWhiteSpace)) throw SwException.InvalidArgument("'names' contains an empty name.");
        return session.RunAsync("drw_delete", ctx => Delete(ctx, names, dryRun), dryRun ? RunOptions.Read(doc) : RunOptions.Write(doc), ct);
    }

    /// <summary>Something drw_delete can remove, with how to select it again (selection objects go stale between deletes).</summary>
    private sealed record DeleteTarget(string Name, string Kind, string? View, Func<IModelDoc2, bool> Select, Func<IDrawingDoc, bool> Exists);

    private static DrwDeleteResult Delete(SwContext ctx, IReadOnlyList<string> names, bool dryRun)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var targets = names.Select((n, i) => DeleteTargetFor(drw, n.Trim(), $"names[{i}]")).ToList();
        var dup = targets.GroupBy(t => (t.Kind, t.Name, t.View)).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw SwException.InvalidArgument($"'{dup.Key.Name}' is named twice.");
        var result = targets.Select(t => new DrwDeleted { Name = t.Name, Kind = t.Kind, View = t.View }).ToList();
        if (dryRun)
            return new DrwDeleteResult { Doc = ctx.Title(d), Deleted = result, Checks = ["dry run: nothing was deleted"] };
        var failed = new List<string>();
        var checks = new List<string>();
        foreach (var t in targets)
        {
            try
            {
                d.ClearSelection2(true);
                bool sel = t.Select(d);
                bool del = sel && d.Extension.DeleteSelection2(0);
                d.ClearSelection2(true);
                if (!sel) failed.Add($"{t.Name} (could not be selected)");
                else if (!del || t.Exists(drw)) failed.Add($"{t.Name} (still there after the delete)");
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e)) { failed.Add($"{t.Name} ({e.Message})"); }
        }
        d.GraphicsRedraw2();
        if (failed.Count > 0)
        {
            var done = result.Where(r => !failed.Any(f => f.StartsWith(r.Name + " ", StringComparison.Ordinal))).ToList();
            throw new SwException(ErrorCodes.VerifyFailed,
                $"Not deleted: {string.Join("; ", failed.Take(6))}.{(done.Count > 0 ? $" Deleted: {string.Join(", ", done.Select(x => x.Name))} (doc_undo puts them back)." : "")}",
                "Check the names with drw_annotation_list / drw_info; views with dependent views delete only with their children named too.");
        }
        checks.Add($"{targets.Count} item(s) gone after the delete");
        return new DrwDeleteResult { Doc = ctx.Title(d), Deleted = result, Checks = checks };
    }

    /// <summary>
    /// Resolves a name for drw_delete: a view ("Drawing View3"), an annotation (drw_annotation_list names), a detail circle
    /// or section line ("Detail Circle1", "Section Line A" or its label "A"), or a sketch segment of a view's sketch
    /// ("Line1@Drawing View1").
    /// </summary>
    private static DeleteTarget DeleteTargetFor(IDrawingDoc drw, string name, string arg)
    {
        var views = AllViews(drw);
        string? cur = drw.DrwCurrentSheet()?.GetName();
        var view = views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        if (view is not null)
        {
            if (view.View.Type == (int)swDrawingViewTypes_e.swDrawingSheet)
                throw SwException.InvalidArgument($"'{name}' is a sheet; drw_delete doesn't delete sheets.");
            string vn = view.Name;
            return new DeleteTarget(vn, "view", null,
                d => d.Extension.SelectByID2(vn, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0),
                dr => AllViews(dr).Any(v => v.Name == vn));
        }
        // Detail circles and section lines of the current sheet's views.
        foreach (var vh in views.Where(v => string.Equals(v.Sheet, cur, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var dc in ComArgs.Objects<IDetailCircle>(Safe(() => (object?)vh.View.GetDetailCircles(), null)))
            {
                string n = Safe(() => dc.GetName(), "") ?? "", label = Safe(() => dc.GetLabel(), "") ?? "";
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || string.Equals($"Detail Circle {label}", name, StringComparison.OrdinalIgnoreCase))
                {
                    string owner = vh.Name;
                    return new DeleteTarget(n, "detail_circle", owner,
                        d => FindDetailCircle(ctx: null, (IDrawingDoc)d, owner, n)?.Select(false, null) ?? false,
                        dr => FindDetailCircle(null, dr, owner, n) is not null);
                }
            }
            foreach (var sl in ComArgs.Objects<IDrSection>(Safe(() => (object?)vh.View.GetSectionLines(), null)))
            {
                string n = Safe(() => sl.GetName(), "") ?? "", label = Safe(() => sl.GetLabel(), "") ?? "";
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || string.Equals($"Section Line {label}", name, StringComparison.OrdinalIgnoreCase))
                {
                    string owner = vh.Name;
                    return new DeleteTarget(n, "section_line", owner,
                        d => d.Extension.SelectByID2(n, "SECTIONLINE", 0, 0, 0, false, 0, null, 0),
                        dr => AllViews(dr).Where(v => v.Name == owner).Any(v => ComArgs.Objects<IDrSection>(Safe(() => (object?)v.View.GetSectionLines(), null))
                            .Any(s => Safe(() => s.GetName(), "") == n)));
                }
            }
        }
        // Sketch segments: "Line1@Drawing View1" (or a bare name when exactly one view sketch has it).
        int at = name.LastIndexOf('@');
        string segName = at > 0 ? name[..at] : name, segView = at > 0 ? name[(at + 1)..] : "";
        var segHits = views.Where(v => string.Equals(v.Sheet, cur, StringComparison.OrdinalIgnoreCase) && (segView.Length == 0 || string.Equals(v.Name, segView, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(v => (v.View.DrwSketch()?.Segments() ?? []).Where(s => string.Equals(Safe(() => s.GetName(), ""), segName, StringComparison.OrdinalIgnoreCase)).Select(s => (v, s)))
            .ToList();
        // Annotations are tried before bare segment names (a dimension "RD1@Drawing View1" also has an '@').
        var anns = SheetAnnotations(drw).Where(a => DrawingEdit.NameMatches(name, Safe(() => a.Ann.GetName(), "") ?? "", a.Dim is null ? null : a.Name, a.View)).ToList();
        if (anns.Count == 1)
        {
            var a = anns[0];
            string an = Safe(() => a.Ann.GetName(), "") ?? "", av = a.View, full = a.Name;
            return new DeleteTarget(a.Name, a.Type, a.View,
                _ => FindByName(drw, full, an, av)?.Ann.Select2(false, 0) ?? false,
                dr => FindByName(dr, full, an, av) is not null);
        }
        if (anns.Count > 1)
            throw new SwException(ErrorCodes.EntityAmbiguous, $"{arg} '{name}' names {anns.Count} annotations.",
                $"Qualify it with the view: {string.Join(", ", anns.Take(6).Select(h => $"{DrawingEdit.ShortDim(h.Name)}@{h.View}"))}.");
        if (segHits.Count == 1)
        {
            var (v, s) = segHits[0];
            string vn = v.Name, sn = Safe(() => s.GetName(), "") ?? segName;
            return new DeleteTarget($"{sn}@{vn}", "sketch_segment", vn,
                _ => Segment(drw, vn, sn)?.Select4(false, null) ?? false,
                dr => Segment(dr, vn, sn) is not null);
        }
        if (segHits.Count > 1)
            throw new SwException(ErrorCodes.EntityAmbiguous, $"{arg} '{name}' is a sketch segment in {segHits.Count} views.",
                $"Qualify it: {string.Join(", ", segHits.Take(6).Select(h => $"{segName}@{h.v.Name}"))}.");
        throw new SwException(ErrorCodes.EntityNotFound, $"{arg}: nothing named '{name}' on the current sheet (views, annotations, detail circles, section lines, view sketch segments).",
            "drw_info lists views; drw_annotation_list lists annotations; section lines are 'Section Line A' (or their name), detail circles 'Detail Circle B'.");
    }

    private static AnnHit? FindByName(IDrawingDoc drw, string full, string annName, string view) =>
        SheetAnnotations(drw).FirstOrDefault(a => a.View == view && (a.Name == full || Safe(() => a.Ann.GetName(), "") == annName));

    private static IDetailCircle? FindDetailCircle(SwContext? ctx, IDrawingDoc drw, string view, string name) =>
        AllViews(drw).Where(v => v.Name == view)
            .SelectMany(v => ComArgs.Objects<IDetailCircle>(Safe(() => (object?)v.View.GetDetailCircles(), null)))
            .FirstOrDefault(c => Safe(() => c.GetName(), "") == name);

    private static ISketchSegment? Segment(IDrawingDoc drw, string view, string name) =>
        AllViews(drw).Where(v => v.Name == view).SelectMany(v => v.View.DrwSketch()?.Segments() ?? [])
            .FirstOrDefault(s => Safe(() => s.GetName(), "") == name);
}
