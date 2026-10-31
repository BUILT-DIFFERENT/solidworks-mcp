using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>drw_view_set: what to change on an existing view; null leaves a setting as it is.</summary>
public sealed record DrwViewSetRequest
{
    public string? Doc { get; init; }
    public string? View { get; init; }
    /// <summary>Where the view's outline centre goes [x, y] sheet mm.</summary>
    public double[]? CenterMm { get; init; }
    /// <summary>Where the view's outline goes [xMin, yMin, xMax, yMax]: its lower-left corner is matched (the size follows the scale).</summary>
    public double[]? BoundsMm { get; init; }
    /// <summary>"1:2", or "sheet" to follow the sheet scale.</summary>
    public string? Scale { get; init; }
    public string? Configuration { get; init; }
    /// <summary>none (break the alignment) | default (align with the parent again) | horizontal | vertical (with alignWith).</summary>
    public string? Alignment { get; init; }
    public string? AlignWith { get; init; }
    public bool? Locked { get; init; }
    public bool DryRun { get; init; }
}

public sealed class DrwViewSetResult : ToolResult
{
    public required string View { get; init; }
    public required IReadOnlyList<string> Changed { get; init; }
    /// <summary>The view as it reads now (dry run: as it is, with the plan in <see cref="Changed"/>).</summary>
    public required DrwView Now { get; init; }
    public bool? DryRun { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed partial class DrawingService
{
    // ================================================================== drw_view_set

    public Task<DrwViewSetResult> ViewSetAsync(DrwViewSetRequest q, CancellationToken ct = default)
    {
        if (q.CenterMm is null && q.BoundsMm is null && q.Scale is null && q.Configuration is null && q.Alignment is null && q.Locked is null)
            throw SwException.InvalidArgument("Nothing to change.", "Pass centerMm or boundsMm (move), scale, configuration, alignment and/or locked.");
        if (q.CenterMm is not null && q.BoundsMm is not null) throw SwException.InvalidArgument("Pass centerMm or boundsMm, not both.");
        if (q.CenterMm is not null) Sheet2(q.CenterMm, "centerMm");
        if (q.BoundsMm is not null && (q.BoundsMm.Length != 4 || q.BoundsMm.Any(v => !double.IsFinite(v)) || q.BoundsMm[2] <= q.BoundsMm[0] || q.BoundsMm[3] <= q.BoundsMm[1]))
            throw SwException.InvalidArgument("'boundsMm' must be [xMin, yMin, xMax, yMax] in sheet mm.");
        double? scale = null;
        bool sheetScale = false;
        if (q.Scale is not null)
        {
            if (DrawingLayout.Norm(q.Scale) is "sheet" or "sheet_scale") sheetScale = true;
            else
            {
                var (n, dd) = DrawingLayout.ParseScale(q.Scale, "scale");
                scale = n / dd;
            }
        }
        string? align = DrawingLayout.Norm(q.Alignment) switch
        {
            null => null,
            "none" or "break" or "remove" or "off" => "none",
            "default" or "parent" or "restore" => "default",
            "horizontal" or "horizontal_center" => "horizontal",
            "vertical" or "vertical_center" => "vertical",
            _ => throw SwException.InvalidArgument($"Unknown alignment '{q.Alignment}'.", "Use none (break it), default (with the parent), horizontal or vertical (with alignWith)."),
        };
        if (align is "horizontal" or "vertical" && string.IsNullOrWhiteSpace(q.AlignWith))
            throw SwException.InvalidArgument($"alignment '{align}' needs 'alignWith': the view to align to.");
        return session.RunAsync("drw_view_set", ctx => ViewSet(ctx, q, scale, sheetScale, align),
            (q.DryRun ? RunOptions.Read(q.Doc) : RunOptions.Write(q.Doc)) with { Timeout = Longer(session, 90) }, ct);
    }

    private static DrwViewSetResult ViewSet(SwContext ctx, DrwViewSetRequest q, double? scale, bool sheetScale, string? align)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var hit = FindView(ctx, drw, q.View, "view");
        if (hit.View.Type == (int)swDrawingViewTypes_e.swDrawingSheet) throw SwException.InvalidArgument($"'{hit.Name}' is a sheet, not a view.");
        var v = hit.View;
        var changed = new List<string>();
        var checks = new List<string>();
        var start = Outline(hit);
        SheetRect? target = q.BoundsMm is { } b ? new SheetRect(b[0], b[1], b[2], b[3]) : null;
        (double X, double Y)? center = q.CenterMm is null ? null : Sheet2(q.CenterMm, "centerMm");

        string? config = null;
        if (q.Configuration is not null)
        {
            var model = ModelOf(hit);
            var names = model.ConfigNames();
            config = names.FirstOrDefault(n => string.Equals(n, q.Configuration.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? throw SwException.InvalidArgument($"'{Path.GetFileName(model.GetPathName() ?? "")}' has no configuration '{q.Configuration}'.",
                         $"Its configurations: {string.Join(", ", names.Take(20))}.");
        }
        ViewHit? with = q.AlignWith is null ? null : FindView(ctx, drw, q.AlignWith, "alignWith");

        if (q.DryRun)
        {
            if (scale is double s0) changed.Add($"scale: {DrawingLayout.FormatScale(v.ScaleDecimal)} → {DrawingLayout.FormatScale(s0)} (the outline grows by {Math.Round(s0 / v.ScaleDecimal, 3)}x about its centre)");
            if (sheetScale) changed.Add("scale: follow the sheet");
            if (config is not null) changed.Add($"configuration: '{v.ReferencedConfiguration}' → '{config}'");
            if (align is not null) changed.Add($"alignment: {align}{(with is null ? "" : $" with '{with.Name}'")}");
            if (target is not null || center is not null)
            {
                var size = scale is double s1 ? SheetRect.Around(start.Cx, start.Cy, start.W * s1 / v.ScaleDecimal, start.H * s1 / v.ScaleDecimal) : start;
                var (dx, dy) = DrawingEdit.OutlineCorrection(size, center?.X, center?.Y, target);
                var moved = new SheetRect(size.X0 + dx, size.Y0 + dy, size.X1 + dx, size.Y1 + dy);
                changed.Add($"outline: [{Fmt(start.ToArray())}] → [{Fmt(moved.ToArray())}] mm");
                var sheet = SheetOf(drw, hit);
                var clashes = Occupied(sheet).Where(r => !SameRect(r, start) && r.Overlaps(moved)).ToList();
                if (clashes.Count > 0) ctx.Warn($"The moved outline would overlap {clashes.Count} other view(s).");
                if (!moved.Inside(Frame(sheet), 0.5)) ctx.Warn("The moved outline would reach outside the drawing frame.");
                if (Safe(() => v.GetAlignment(), 0) != 0 && align != "none")
                    ctx.Warn($"'{hit.Name}' is aligned to another view, so it can only move along that alignment; pass alignment 'none' to move it freely.");
            }
            return new DrwViewSetResult { Doc = ctx.Title(d), View = hit.Name, Changed = changed, Now = ViewInfo(ctx, v, false), DryRun = true, Checks = ["dry run: nothing changed"] };
        }

        // Alignment first: an aligned view can only slide along its alignment.
        if (align is not null)
        {
            int before = Safe(() => v.GetAlignment(), -1);
            switch (align)
            {
                case "none":
                    v.RemoveAlignment();
                    break;
                case "default":
                {
                    var parent = v.DrwBaseView() ?? throw SwException.InvalidArgument($"'{hit.Name}' has no parent view to align with.", "Pass alignWith and alignment horizontal or vertical.");
                    if (!v.AlignWithView((int)swAlignViewTypes_e.swDefaultViewAlignment, (View)(object)parent)) ctx.Warn("AlignWithView returned false.");
                    break;
                }
                default:
                {
                    int type = align == "horizontal" ? (int)swAlignViewTypes_e.swAlignViewHorizontalCenter : (int)swAlignViewTypes_e.swAlignViewVerticalCenter;
                    if (!v.AlignWithView(type, (View)(object)with!.View)) ctx.Warn("AlignWithView returned false.");
                    break;
                }
            }
            int after = Safe(() => v.GetAlignment(), -1);
            if (align == "none" && after > 0)
                throw new SwException(ErrorCodes.VerifyFailed, $"'{hit.Name}' is still aligned (alignment {after}) after RemoveAlignment.");
            changed.Add($"alignment: {before} → {after} ({align})");
        }
        if (config is not null)
        {
            string old = v.ReferencedConfiguration ?? "";
            v.ReferencedConfiguration = config;
            d.EditRebuild3();
            string now = v.ReferencedConfiguration ?? "";
            if (!string.Equals(now, config, StringComparison.OrdinalIgnoreCase))
            {
                v.ReferencedConfiguration = old;
                throw new SwException(ErrorCodes.VerifyFailed, $"'{hit.Name}' shows configuration '{now}', not '{config}'; it was put back to '{old}'.");
            }
            changed.Add($"configuration: '{old}' → '{now}'");
            checks.Add("configuration read back");
        }
        if (scale is not null || sheetScale)
        {
            string old = DrawingLayout.FormatScale(v.ScaleDecimal);
            var keep = Outline(hit);
            if (sheetScale) v.UseSheetScale = 1;
            else
            {
                v.UseSheetScale = 0;
                v.ScaleDecimal = scale!.Value;
            }
            d.EditRebuild3();
            if (scale is double s && !SameScale(v.ScaleDecimal, s))
                throw new SwException(ErrorCodes.VerifyFailed, $"'{hit.Name}' kept scale {DrawingLayout.FormatScale(v.ScaleDecimal)} instead of {DrawingLayout.FormatScale(s)}.");
            changed.Add($"scale: {old} → {DrawingLayout.FormatScale(v.ScaleDecimal)}{(sheetScale ? " (the sheet's)" : "")}");
            // A new scale grows the view about its position, which isn't its outline centre for every type: keep the centre.
            center ??= target is null ? (keep.Cx, keep.Cy) : null;
        }
        if (center is not null || target is not null)
        {
            var now = Outline(hit);
            for (int i = 0; i < 4; i++)
            {
                var (dx, dy) = DrawingEdit.OutlineCorrection(now, center?.X, center?.Y, target);
                if (Math.Abs(dx) < 0.05 && Math.Abs(dy) < 0.05) break;
                var p = v.DrwPosition();
                if (p.Length < 2) throw new SwException(ErrorCodes.Internal, $"The position of '{hit.Name}' could not be read.");
                v.DrwMoveTo(p[0] + Units.M(dx), p[1] + Units.M(dy));
                var next = Outline(hit);
                if (SameRect(next, now))
                {
                    ctx.Warn($"'{hit.Name}' did not move{(Safe(() => v.GetAlignment(), 0) != 0 ? " (it is aligned to another view; pass alignment 'none')" : v.PositionLocked ? " (its position is locked; pass locked:false)" : "")}.");
                    break;
                }
                now = next;
            }
            var (rx, ry) = DrawingEdit.OutlineCorrection(now, center?.X, center?.Y, target);
            if (Math.Abs(rx) > 0.05 || Math.Abs(ry) > 0.05)
                ctx.Warn($"'{hit.Name}' ended {Math.Round(Math.Sqrt(rx * rx + ry * ry), 2)} mm from the target (an alignment or the sheet may hold it).");
            else checks.Add(target is null ? "outline centre read back at the target" : "outline corner read back at the target");
            changed.Add($"outline: [{Fmt(start.ToArray())}] → [{Fmt(now.ToArray())}] mm");
            OverlapCheck(ctx, SheetOf(drw, hit), [v], checks);
        }
        if (q.Locked is bool lk)
        {
            bool old = v.PositionLocked;
            v.PositionLocked = lk;
            if (v.PositionLocked != lk) throw new SwException(ErrorCodes.VerifyFailed, $"'{hit.Name}' position lock reads {v.PositionLocked}.");
            changed.Add($"locked: {old} → {lk}");
        }
        d.GraphicsRedraw2();
        return new DrwViewSetResult { Doc = ctx.Title(d), View = hit.Name, Changed = changed, Now = ViewInfo(ctx, v, false), Checks = checks };
    }

    private static bool SameRect(SheetRect a, SheetRect b) =>
        Math.Abs(a.X0 - b.X0) < 1e-3 && Math.Abs(a.Y0 - b.Y0) < 1e-3 && Math.Abs(a.X1 - b.X1) < 1e-3 && Math.Abs(a.Y1 - b.Y1) < 1e-3;

    // ================================================================== section / detail helpers

    /// <summary>
    /// After a section or detail view: leaves the parent view's sketch if SOLIDWORKS is still editing it (it blanked the
    /// sheet and blocked rebuild and undo) and makes the sheet active again. Reports what it did.
    /// </summary>
    private static void LeaveViewSketch(SwContext ctx, IModelDoc2 d, IDrawingDoc drw, ISheet sheet, List<string> checks)
    {
        string? sheetSketch = SketchName(drw.DrwSheetView()?.DrwSketch());
        for (int i = 0; i < 2; i++)
        {
            var open = Safe(() => d.SketchManager.ActiveSketch, null);
            string? name = SketchName(open);
            if (open is null || name is null || name == sheetSketch) break;
            try { d.SketchManager.InsertSketch(true); }
            catch (COMException e) when (!ComArgs.IsDisconnect(e)) { break; }
            if (i == 0) checks.Add($"closed the view sketch '{name}' the view command left open");
        }
        try
        {
            string sn = sheet.GetName();
            if (drw.DrwActiveView() is IView av && av.Type != (int)swDrawingViewTypes_e.swDrawingSheet && !drw.ActivateView(sn)) drw.ActivateSheet(sn);
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* best effort */ }
        string? still = SketchName(Safe(() => d.SketchManager.ActiveSketch, null));
        if (still is not null && still != sheetSketch)
            ctx.Warn($"Sketch '{still}' is still open in the drawing; exit it in SOLIDWORKS (rebuild and undo wait for it).");
    }

    /// <summary>
    /// Components of an assembly view by name ("bracket-1", or "sub-1/bracket-1" inside a subassembly), for
    /// CreateSectionViewAt5's excluded components. Unknown names list what the view's assembly has.
    /// </summary>
    private static List<IComponent2> ViewComponents(ViewHit hit, IReadOnlyList<string> names)
    {
        var model = ModelOf(hit);
        if (model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            throw SwException.InvalidArgument($"'excludeComponents' needs a view of an assembly; '{hit.Name}' shows a part.");
        var asm = (IAssemblyDoc)model;
        var all = ComArgs.Objects<IComponent2>((object?)asm.GetComponents(false));
        var found = new List<IComponent2>();
        foreach (var (n, i) in names.Select((n, i) => (n.Trim(), i)))
        {
            var exact = all.Where(c => string.Equals(c.Name2, n, StringComparison.OrdinalIgnoreCase)).ToList();
            var leaf = exact.Count > 0 ? exact : all.Where(c => string.Equals(c.Name2?.Split('/').Last(), n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (leaf.Count == 1) { found.Add(leaf[0]); continue; }
            if (leaf.Count > 1)
                throw new SwException(ErrorCodes.EntityAmbiguous, $"excludeComponents[{i}] '{n}' matches {leaf.Count} components.",
                    $"Use the full path: {string.Join(", ", leaf.Take(6).Select(c => c.Name2))}.");
            throw new SwException(ErrorCodes.EntityNotFound, $"excludeComponents[{i}]: the assembly has no component '{n}'.",
                $"Components: {string.Join(", ", all.Take(12).Select(c => c.Name2))}{(all.Count > 12 ? ", …" : "")} (asm_component_list allLevels:true).");
        }
        return found;
    }

    /// <summary>A model point (mm) in a view, as a sheet point (mm), through the view's model-to-sheet transform.</summary>
    private static (double X, double Y) ModelToSheet(ViewHit hit, double[] modelMm, string arg)
    {
        var p = Units.PointM(modelMm, arg);
        var xf = hit.View.DrwModelToSheet();
        if (xf.Length < 13) throw new SwException(ErrorCodes.NotSupported, $"View '{hit.Name}' gives no model-to-sheet transform, so '{arg}' can't be placed in it.");
        var s = new Xform(xf).ApplyPoint(p);
        return (s.X * 1000, s.Y * 1000);
    }
}
