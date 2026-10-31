using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Services;

/// <summary>A baseline or chain set of drawing dimensions from one datum.</summary>
public sealed class DrwDimensionSetResult : ToolResult
{
    public required string View { get; init; }
    /// <summary>baseline | chain.</summary>
    public required string Kind { get; init; }
    public required IReadOnlyList<DrwDimensionResult> Dimensions { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>drw_dimensions_arrange: dimensions stacked on one side of a view.</summary>
public sealed record DrwArrangeRequest
{
    public string? Doc { get; init; }
    public string? View { get; init; }
    /// <summary>The dimensions to stack (names); default: those of the view whose text is outside it on that side.</summary>
    public IReadOnlyList<string>? Dimensions { get; init; }
    /// <summary>above | below | left | right.</summary>
    public string? Side { get; init; }
    public double SpacingMm { get; init; } = 7;
    public double OffsetMm { get; init; } = 10;
    public bool DryRun { get; init; }
}

public sealed class DrwMoved
{
    public required string Name { get; init; }
    public required double[] FromMm { get; init; }
    public required double[] ToMm { get; init; }
}

public sealed class DrwArrangeResult : ToolResult
{
    public required string View { get; init; }
    public required string Side { get; init; }
    public required IReadOnlyList<DrwMoved> Moved { get; init; }
    public bool? DryRun { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>One step of drw_batch: an editing tool's name (without the drw_ prefix) and its arguments.</summary>
public sealed record DrwBatchStep
{
    /// <summary>dimension_set | annotation_set | delete | view_set | table_set | dimensions_arrange.</summary>
    public string? Op { get; init; }
    public JsonElement? Args { get; init; }
}

public sealed class DrwBatchResult : ToolResult
{
    public required IReadOnlyList<object> Steps { get; init; }
    /// <summary>The batch was one undo step (doc_undo takes it back at once).</summary>
    public bool OneUndoStep { get; init; }
    /// <summary>Dry run: the steps ran, were checked and were undone.</summary>
    public bool? DryRun { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed partial class DrawingService
{
    // ================================================================== baseline / chain sets (drw_dimension_add)

    /// <summary>
    /// A baseline (every target from the datum) or chain (each target from the previous one) set of horizontal or vertical
    /// dimensions in one call, stacked on one side of the view without overlap. Each dimension is verified as
    /// drw_dimension_add does; on any failure the set made so far is removed.
    /// </summary>
    public Task<DrwDimensionSetResult> DimensionSetAddAsync(string? view, EntityRef? datum, IReadOnlyList<EntityRef>? targets, string? kind,
        string? direction, string? side, double spacingMm, string? doc, CancellationToken ct = default)
    {
        string k = DrawingLayout.Norm(kind) switch
        {
            "baseline" => "baseline",
            "chain" => "chain",
            _ => throw SwException.InvalidArgument($"A set of dimensions is kind baseline or chain (got '{kind}')."),
        };
        if (datum is null) throw SwException.InvalidArgument("'a' is the datum: the edge or vertex every dimension measures from.");
        if (targets is null || targets.Count == 0) throw SwException.InvalidArgument("'targets' lists the edges or vertices to dimension to.");
        if (targets.Count > 30) throw SwException.InvalidArgument($"At most 30 targets (got {targets.Count}).");
        string dir = DrawingLayout.Norm(direction) switch
        {
            "horizontal" or "x" => "horizontal",
            "vertical" or "y" => "vertical",
            _ => throw SwException.InvalidArgument("A baseline or chain set needs direction: horizontal (x distances) or vertical (y distances)."),
        };
        string sd = DrawingLayout.Norm(side) ?? (dir == "horizontal" ? "above" : "right");
        if (dir == "horizontal" && sd is not ("above" or "below") || dir == "vertical" && sd is not ("left" or "right"))
            throw SwException.InvalidArgument($"Horizontal dimensions stack above or below the view, vertical ones left or right (got side '{side}').");
        DrawingEdit.Stack([], new SheetRect(0, 0, 1, 1), sd, 10, spacingMm); // validates spacing and side
        if (k == "baseline" && 10 + (targets.Count - 1) * spacingMm > 200)
            throw SwException.InvalidArgument($"{targets.Count} baseline rows {spacingMm} mm apart reach {10 + (targets.Count - 1) * spacingMm} mm from the view (at most 200).",
                "Use a smaller spacingMm or fewer targets per call.");
        var options = RunOptions.Write(doc) with { DialogRules = [DialogRules.ModifyDimension], Timeout = Longer(session, 30 + 6 * targets.Count) };
        return session.RunAsync("drw_dimension_add", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var hit = FindView(ctx, drw, view, "view");
            var model = ModelOf(hit);
            var xf = hit.View.DrwModelToSheet();
            if (xf.Length < 13) throw new SwException(ErrorCodes.NotSupported, $"View '{hit.Name}' gives no model-to-sheet transform.");
            var t = new Xform(xf);
            bool part = model.GetType() == (int)swDocumentTypes_e.swDocPART;
            var outline = Outline(hit);
            // Sheet positions of the picks: text goes midway along, stacked (baseline) or in one row (chain).
            (double X, double Y) Pick(EntityRef r, string arg)
            {
                var g = Seen(EntityResolver.Resolve(ctx, model, r, arg, EntityKind.Edge, EntityKind.Vertex), t, part, arg);
                return (g.Pick.X * 1000, g.Pick.Y * 1000);
            }
            var p0 = Pick(datum, "a");
            var picks = targets.Select((r, i) => Pick(r, $"targets[{i}]")).ToList();
            var made = new List<DrwDimensionResult>();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var from = k == "baseline" || i == 0 ? p0 : picks[i - 1];
                    var to = picks[i];
                    double along = dir == "horizontal" ? (from.X + to.X) / 2 : (from.Y + to.Y) / 2;
                    double row = k == "baseline" ? 10 + i * spacingMm : 10;
                    var slot = DrawingEdit.Stack([(dir == "horizontal" ? along : 0, dir == "horizontal" ? 0 : along, 0)], outline, sd, row, spacingMm)[0];
                    var a = k == "baseline" || i == 0 ? datum : targets[i - 1];
                    made.Add(DimensionAdd(ctx, hit.Name, a, targets[i], dir, (slot.X, slot.Y)));
                }
            }
            catch (Exception ex) when (made.Count > 0 && !ComArgs.IsDisconnect(ex))
            {
                int removed = 0;
                foreach (var m in made)
                {
                    var an = SheetAnnotations(drw, [hit.Name]).FirstOrDefault(x => x.Dim is not null && DrawingEdit.ShortDim(x.Name) == DrawingEdit.ShortDim(m.Dimension));
                    if (an is not null && DeleteAnnotation(d, an.Ann)) removed++;
                }
                throw Rolled(ex, $"Removed {removed} of the {made.Count} dimension(s) this set had made.");
            }
            var checks = new List<string> { $"{made.Count} {k} dimension(s), each value checked against the geometry" };
            return new DrwDimensionSetResult { Doc = ctx.Title(d), View = hit.Name, Kind = k, Dimensions = made, Checks = checks };
        }, options, ct);
    }

    // ================================================================== drw_dimensions_arrange

    public Task<DrwArrangeResult> ArrangeAsync(DrwArrangeRequest q, CancellationToken ct = default)
    {
        DrawingEdit.Stack([], new SheetRect(0, 0, 1, 1), q.Side ?? "", q.OffsetMm, q.SpacingMm); // validates
        return session.RunAsync("drw_dimensions_arrange", ctx => Arrange(ctx, q), q.DryRun ? RunOptions.Read(q.Doc) : RunOptions.Write(q.Doc), ct);
    }

    private static DrwArrangeResult Arrange(SwContext ctx, DrwArrangeRequest q)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var hit = FindView(ctx, drw, q.View, "view");
        var outline = Outline(hit);
        string side = DrawingLayout.Norm(q.Side) switch { "top" or "up" => "above", "bottom" or "down" => "below", { } s => s, _ => "above" };
        var dims = SheetAnnotations(drw, [hit.Name]).Where(a => a.Dim is not null).ToList();
        List<AnnHit> pick;
        if (q.Dimensions is { Count: > 0 } names)
        {
            pick = names.Select((n, i) => dims.FirstOrDefault(a => DrawingEdit.NameMatches(n, Safe(() => a.Ann.GetName(), "") ?? "", a.Name, a.View))
                                          ?? throw new SwException(ErrorCodes.EntityNotFound, $"dimensions[{i}]: '{n}' is not a dimension of '{hit.Name}'.",
                                              $"Its dimensions: {string.Join(", ", dims.Take(10).Select(x => x.Name))}.")).ToList();
        }
        else
        {
            pick = dims.Where(a => a.Ann.DrwPosition() is { Length: >= 2 } p && side switch
            {
                "above" => p[1] * 1000 > outline.Y1,
                "below" => p[1] * 1000 < outline.Y0,
                "left" => p[0] * 1000 < outline.X0,
                _ => p[0] * 1000 > outline.X1,
            }).ToList();
            if (pick.Count == 0)
                throw SwException.InvalidArgument($"No dimension text of '{hit.Name}' lies {side} the view.", "Name the dimensions to stack (drw_annotation_list).");
        }
        var data = pick.Select(a =>
        {
            var p = a.Ann.DrwPosition();
            var info = DimInfo(ctx, a.Dim!);
            return (X: p[0] * 1000, Y: p[1] * 1000, Value: info?.Value ?? 0);
        }).ToList();
        var slots = DrawingEdit.Stack(data, outline, side, q.OffsetMm, q.SpacingMm);
        var moved = new List<DrwMoved>();
        var checks = new List<string>();
        var frame = Frame(SheetOf(drw, hit));
        foreach (var sl in slots)
        {
            var a = pick[sl.Index];
            var from = data[sl.Index];
            if (!frame.Contains(sl.X, sl.Y)) ctx.Warn($"'{a.Name}' would go to [{Fmt([sl.X, sl.Y])}] mm, outside the drawing frame (use a smaller spacing or another side).");
            if (!q.DryRun)
            {
                a.Ann.SetPosition2(Units.M(sl.X), Units.M(sl.Y), 0);
                var now = a.Ann.DrwPosition();
                if (now.Length < 2 || Math.Abs(now[0] * 1000 - sl.X) > 0.1 || Math.Abs(now[1] * 1000 - sl.Y) > 0.1)
                    ctx.Warn($"'{a.Name}' reads at [{(now.Length >= 2 ? Fmt([now[0] * 1000, now[1] * 1000]) : "?")}] mm after the move.");
            }
            moved.Add(new DrwMoved { Name = a.Name, FromMm = [Math.Round(from.X, 3), Math.Round(from.Y, 3)], ToMm = [Math.Round(sl.X, 3), Math.Round(sl.Y, 3)] });
        }
        if (!q.DryRun)
        {
            d.GraphicsRedraw2();
            var boxes = pick.Select(Box).OfType<SheetRect>().ToList();
            int overlaps = 0;
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                    if (boxes[i].Overlaps(boxes[j], -0.2)) overlaps++;
            if (overlaps == 0) checks.Add($"{moved.Count} dimension(s) stacked {side} '{hit.Name}' without overlapping text");
            else ctx.Warn($"{overlaps} pair(s) of the stacked dimensions still overlap; raise spacingMm.");
        }
        else checks.Add("dry run: nothing moved");
        return new DrwArrangeResult { Doc = ctx.Title(d), View = hit.Name, Side = side, Moved = moved, DryRun = q.DryRun ? true : null, Checks = checks };
    }

    // ================================================================== center marks and centerlines on chosen features

    /// <summary>
    /// drw_center_marks on chosen entities: a center mark on each circular edge (IDrawingDoc.InsertCenterMark3 with the
    /// edge selected), or a centerline on each cylindrical or conical face, e.g. a port's axis in a section view
    /// (InsertCenterLine2 with the face selected). Verified: the view gained one per entity.
    /// </summary>
    public Task<DrwCountResult> CenterMarksAtAsync(string? view, IReadOnlyList<EntityRef> at, string? kind, string? doc, CancellationToken ct = default)
    {
        string k = DrawingLayout.Norm(kind) switch
        {
            null or "center_mark" or "centermark" => "center_mark",
            "centerline" or "center_line" or "axis" => "centerline",
            _ => throw SwException.InvalidArgument($"Unknown kind '{kind}'.", "Use center_mark (circular edges) or centerline (cylindrical faces)."),
        };
        if (at.Count > 50) throw SwException.InvalidArgument($"At most 50 entities (got {at.Count}).");
        if (view is null) throw SwException.InvalidArgument("'at' needs 'view' (one view): the view whose model the entities belong to.");
        return session.RunAsync("drw_center_marks", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var hit = FindView(ctx, drw, view, "view");
            var model = ModelOf(hit);
            int Marks() => k == "centerline" ? Math.Max(SafeCount(hit.View.GetCenterLineCount), 0) : Math.Max(Count(hit.View).CenterMarks, 0);
            int before = Marks();
            Activate(drw, hit);
            int made = 0;
            for (int i = 0; i < at.Count; i++)
            {
                var r = EntityResolver.Resolve(ctx, model, at[i], $"at[{i}]", k == "centerline" ? new[] { EntityKind.Face } : new[] { EntityKind.Edge });
                if (k == "center_mark" && (r.Obj is not IEdge e || Topology.Describe(e).Curve != "circle"))
                    throw SwException.InvalidArgument($"at[{i}] ({r.Label}) is not a circular edge; center marks go on circles (holes seen face-on).");
                if (k == "centerline" && (r.Obj is not IFace2 f || Topology.Describe(f).Surface is not ("cylinder" or "cone")))
                    throw SwException.InvalidArgument($"at[{i}] ({r.Label}) is not a cylindrical or conical face; centerlines go on round faces seen side-on.");
                d.ClearSelection2(true);
                if (!hit.View.SelectEntity(r.Obj, false))
                    throw new SwException(ErrorCodes.EntityNotFound, $"at[{i}] ({r.Label}) could not be selected in '{hit.Name}'.", "It must be visible in that view.");
                object? obj = k == "centerline"
                    ? (object?)drw.InsertCenterLine2()
                    : (object?)drw.InsertCenterMark3((int)swCenterMarkStyle_e.swCenterMark_Single, false, false);
                d.ClearSelection2(true);
                if (obj is null) throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS made no {k.Replace('_', ' ')} on {r.Label} (at[{i}]); {made} made before it stay.");
                made++;
            }
            int after = Marks();
            d.GraphicsRedraw2();
            if (after - before != made) ctx.Warn($"'{hit.Name}' went from {before} to {after} {k.Replace('_', ' ')}(s) for {made} made.");
            return new DrwCountResult { Doc = ctx.Title(d), Added = made, Views = [new DrwViewCount { View = hit.Name, Before = before, After = after, Added = after - before }] };
        }, RunOptions.Write(doc), ct);
    }

    // ================================================================== drw_batch

    private static readonly JsonSerializerOptions StepJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Several editing steps in one call and one undo step (IModelDocExtension.StartRecordingUndoObject /
    /// FinishRecordingUndoObject2): each runs and verifies as its own tool does. A failing step undoes the whole batch;
    /// a dry run undoes it after the last step, so the results show what would happen.
    /// </summary>
    public Task<DrwBatchResult> BatchAsync(IReadOnlyList<DrwBatchStep>? steps, bool dryRun, string? doc, CancellationToken ct = default)
    {
        if (steps is null || steps.Count == 0) throw SwException.InvalidArgument("'steps' is empty.");
        if (steps.Count > 100) throw SwException.InvalidArgument($"At most 100 steps (got {steps.Count}).");
        var plans = steps.Select((s, i) => PlanStep(s, i)).ToList(); // argument errors before any COM call
        return session.RunAsync("drw_batch", ctx =>
        {
            var d = ctx.Doc;
            ctx.Drawing();
            var entry = ctx.DocEntry;
            int stamp = d.GetUpdateStamp();
            int writesBefore = entry.ServerWrites;
            bool chained = entry.ServerWritesStamp == stamp;
            var results = new List<object>();
            d.Extension.StartRecordingUndoObject();
            bool open = true;
            void Finish()
            {
                if (!open) return;
                open = false;
                if (!d.Extension.FinishRecordingUndoObject2($"drw_batch ({plans.Count} steps)", false)) ctx.Warn("The batch could not be closed as one undo step.");
            }
            string UndoAll()
            {
                Finish();
                if (d.GetUpdateStamp() == stamp) return "Nothing had changed.";
                d.EditUndo2(1);
                // This call leaves no undo step of its own: doc_undo's count stays what it was before the batch.
                entry.ServerWrites = chained ? writesBefore : 0;
                entry.ServerWritesStamp = d.GetUpdateStamp();
                return "The batch was undone (one undo step).";
            }
            for (int i = 0; i < plans.Count; i++)
            {
                try { results.Add(plans[i](ctx)); }
                catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
                {
                    string back;
                    try { back = UndoAll(); }
                    catch (Exception u) when (!ComArgs.IsDisconnect(u)) { back = $"Undoing the batch failed ({u.Message}); check the drawing."; }
                    var se = ex as SwException;
                    throw new SwException(se?.Code ?? ErrorCodes.Internal, $"steps[{i}] ({steps[i].Op}): {ex.Message} {back}", se?.Hint, ex);
                }
            }
            Finish();
            var checks = new List<string> { $"{plans.Count} step(s) ran and verified" };
            if (dryRun) checks.Add(UndoAll());
            return new DrwBatchResult { Doc = ctx.Title(d), Steps = results, OneUndoStep = true, DryRun = dryRun ? true : null, Checks = checks };
        }, RunOptions.Write(doc) with { Timeout = Longer(session, 30 + 5 * steps.Count), DialogRules = [DialogRules.ModifyDimension] }, ct);
    }

    /// <summary>A batch step validated and bound to its tool's logic.</summary>
    private static Func<SwContext, object> PlanStep(DrwBatchStep s, int i)
    {
        string op = (DrawingLayout.Norm(s.Op) ?? "").Replace("drw_", "");
        T Args<T>() where T : class
        {
            if (s.Args is not JsonElement e || e.ValueKind != JsonValueKind.Object)
                throw SwException.InvalidArgument($"steps[{i}].args must be an object with {op}'s arguments.");
            try { return e.Deserialize<T>(StepJson) ?? throw SwException.InvalidArgument($"steps[{i}].args is empty."); }
            catch (JsonException ex) { throw SwException.InvalidArgument($"steps[{i}].args: {ex.Message}"); }
        }
        switch (op)
        {
            case "dimension_set":
            {
                var q = Args<DrwDimensionSetRequest>();
                ValidateDimSet(q);
                return ctx => DimensionSet(ctx, q);
            }
            case "annotation_set":
            {
                var q = Args<DrwAnnotationSetRequest>();
                ValidateAnnSet(q);
                return ctx => AnnotationSet(ctx, q);
            }
            case "delete":
            {
                var q = Args<DeleteArgs>();
                if (q.Names is not { Length: > 0 }) throw SwException.InvalidArgument($"steps[{i}]: delete needs names.");
                return ctx => Delete(ctx, q.Names, false);
            }
            case "view_set":
            {
                var q = Args<DrwViewSetRequest>();
                if (q.DryRun) throw SwException.InvalidArgument($"steps[{i}]: dryRun goes on the batch, not on a step.");
                var (scale, sheetScale, align) = ViewSetPlan(q);
                return ctx => ViewSet(ctx, q, scale, sheetScale, align);
            }
            case "table_set":
            {
                var q = Args<DrwTableSetRequest>();
                return ctx => TableSet(ctx, q);
            }
            case "dimensions_arrange":
            {
                var q = Args<DrwArrangeRequest>();
                if (q.DryRun) throw SwException.InvalidArgument($"steps[{i}]: dryRun goes on the batch, not on a step.");
                DrawingEdit.Stack([], new SheetRect(0, 0, 1, 1), q.Side ?? "", q.OffsetMm, q.SpacingMm);
                return ctx => Arrange(ctx, q);
            }
            default:
                throw SwException.InvalidArgument($"steps[{i}].op '{s.Op}' is not a batch step.",
                    "Use dimension_set, annotation_set, delete, view_set, table_set or dimensions_arrange (their tools' arguments go in args).");
        }
    }

    private sealed record DeleteArgs
    {
        public string[]? Names { get; init; }
    }
}
