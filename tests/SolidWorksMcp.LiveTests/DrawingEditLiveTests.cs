using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Editing existing drawings (drw_annotation_list / _set, drw_dimension_set, drw_delete, drw_view_set, drw_check,
/// drw_locate, drw_dimensions_arrange, drw_batch, drw_title_block, doc_standard) and feature_hole_edit, against a live
/// SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1). Oracle: DrawingLiveTests' 100 × 60 × 20 block on the front plane
/// (z 0..20), saved, with a front view; its bottom front edge runs from (-50,-30,20) to (50,-30,20) and is 100 mm long.
/// Status: written with the tools, not yet run live (DESIGN §7.3).
/// </summary>
public class DrawingEditLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double L = 1e-3;
    private static readonly EntityRef Bottom = new() { Type = "edge", At = [0, -30, 20] };
    private static readonly EntityRef Left = new() { Type = "edge", At = [-50, 0, 20] };
    private static readonly EntityRef Right = new() { Type = "edge", At = [50, 0, 20] };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_drwedit_{Guid.NewGuid():N}");
    private readonly List<string> _drawings = [];

    private DrawingService Drw => new(S);
    private DocumentService Docs => new(S);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Directory.CreateDirectory(_dir);
    }

    public override async ValueTask DisposeAsync()
    {
        if (LiveFixture.Enabled)
            foreach (var h in _drawings)
            {
                try { await Docs.CloseAsync(h, save: false, discard: true); }
                catch (SwException) { /* closed by the test */ }
            }
        try { await base.DisposeAsync(); }
        finally
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    private async Task<(string Part, string Path)> BlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await new SketchService(S).StartAsync(doc, new EntityRef { Name = "front" }, false);
        await new SketchService(S).AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(120000, r.Verified.VolumeDeltaMm3, 1e-3);
        var saved = await Docs.SaveAsync(doc, "as", Path.Combine(_dir, name + ".sldprt"), false);
        return (doc, saved.SavedTo!);
    }

    /// <summary>A drawing with the block's front view: (drawing, front view name).</summary>
    private async Task<(string Drawing, DrwView Front)> FrontAsync(string name)
    {
        var (_, path) = await BlockAsync(name);
        var r = await Docs.NewAsync("drawing", "mmgs", null, $"{name}_{Guid.NewGuid().ToString("N")[..6]}");
        _drawings.Add(r.Document.Handle);
        var v = await Drw.ViewsStandardAsync(path, ["front"], null, r.Document.Handle);
        return (r.Document.Handle, v.Views[0]);
    }

    private static async Task<SwException> Fails(string code, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<SwException>(call);
        Assert.True(ex.Code == code, $"expected {code}, got {ex.ToAgentText()}");
        return ex;
    }

    [Fact]
    public async Task Dimension_prefix_tolerance_precision_and_parentheses_read_back()
    {
        var (drw, front) = await FrontAsync("drwedit_dim");
        var dim = await Drw.DimensionAddAsync(front.Name, Bottom, null, "horizontal", null, drw);
        Assert.Equal(100, dim.Value, L);
        var list = await Drw.AnnotationListAsync([front.Name], ["dimension"], 50, 0, drw);
        var row = Assert.Single(list.Annotations);
        Assert.Equal(100, row.Dimension!.Value, L);

        var set = await Drw.DimensionSetAsync(new DrwDimensionSetRequest
        {
            Doc = drw, Name = row.Name, Prefix = "Ø", Suffix = " STROKE", Tolerance = new DimTolerance { Type = "bilateral", PlusMm = 0.1, MinusMm = 0.05 },
            Precision = 1, TolerancePrecision = 2, Parentheses = true,
        });
        var d = set.Now!.Dimension!;
        Assert.Contains("Ø", d.Prefix!.Replace("<MOD-DIAM>", "Ø"));
        Assert.Equal(" STROKE", d.Suffix);
        Assert.Equal("bilateral", d.Tolerance);
        Assert.Equal(0.1, d.PlusMm!.Value, 1e-6);
        Assert.Equal(0.05, d.MinusMm!.Value, 1e-6);
        Assert.Equal(1, d.Precision);
        Assert.Equal(2, d.TolerancePrecision);
        Assert.True(d.Parentheses);

        await Fails(ErrorCodes.EntityNotFound, () => Drw.DimensionSetAsync(new DrwDimensionSetRequest { Doc = drw, Name = "RD99@Nowhere", Prefix = "x" }));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.DimensionSetAsync(new DrwDimensionSetRequest
        {
            Doc = drw, Name = row.Name, Tolerance = new DimTolerance { Type = "bilateral", PlusMm = 0.1 },
        }));
    }

    [Fact]
    public async Task Notes_break_lines_attach_leaders_change_and_delete()
    {
        var (drw, front) = await FrontAsync("drwedit_note");
        var n = await Drw.NoteAddAsync("NOTES:\\n1. DEBURR", [30, 40], null, null, null, drw);
        Assert.Equal("NOTES:\n1. DEBURR", n.Text);
        var leader = await Drw.NoteAddAsync("BREAK EDGE", [front.BoundsMm![2] + 20, front.BoundsMm[1]], null, front.Name, Bottom, drw);
        Assert.NotNull(leader.AttachedTo);

        var set = await Drw.AnnotationSetAsync(new DrwAnnotationSetRequest { Doc = drw, Name = n.Note, Text = "NOTES:\n1. DEBURR\n2. NO SHARP EDGES", AtMm = [40, 50], HeightMm = 5 });
        Assert.Equal("NOTES:\n1. DEBURR\n2. NO SHARP EDGES", set.Now!.Text);
        Assert.Equal(40, set.Now.AtMm![0], 0.01);
        Assert.Equal(5, set.Now.HeightMm!.Value, 1e-3);

        var del = await Drw.DeleteAsync([n.Note, leader.Note], false, drw);
        Assert.Equal(2, del.Deleted.Count);
        var left = await Drw.AnnotationListAsync(null, ["note"], 100, 0, drw);
        Assert.DoesNotContain(left.Annotations, a => a.Name == n.Note || a.Name == leader.Note);
        await Fails(ErrorCodes.EntityNotFound, () => Drw.DeleteAsync([n.Note], false, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.NoteAddAsync("x", [10, 10], null, null, Bottom, drw)); // attachTo needs a view
    }

    [Fact]
    public async Task Views_move_by_their_outline_and_rescale()
    {
        var (drw, front) = await FrontAsync("drwedit_view");
        var dry = await Drw.ViewSetAsync(new DrwViewSetRequest { Doc = drw, View = front.Name, CenterMm = [150, 150], DryRun = true });
        Assert.True(dry.DryRun);
        Assert.Equal(front.BoundsMm, dry.Now.BoundsMm);
        var moved = await Drw.ViewSetAsync(new DrwViewSetRequest { Doc = drw, View = front.Name, CenterMm = [150, 150], Scale = "1:2" });
        var b = moved.Now.BoundsMm!;
        Assert.Equal(150, (b[0] + b[2]) / 2, 0.06);
        Assert.Equal(150, (b[1] + b[3]) / 2, 0.06);
        Assert.Equal("1:2", moved.Now.Scale);
        await Fails(ErrorCodes.InvalidArgument, () => Drw.ViewSetAsync(new DrwViewSetRequest { Doc = drw, View = front.Name }));
    }

    [Fact]
    public async Task Section_leaves_no_sketch_and_undo_works_in_the_drawing()
    {
        var (drw, front) = await FrontAsync("drwedit_section");
        var sec = await Drw.SectionAsync(new DrwSectionRequest { Doc = drw, Parent = front.Name, ThroughModelMm = [0, 0, 10], Orientation = "vertical" });
        Assert.Equal("section", sec.View.Type);
        var note = await Drw.NoteAddAsync("UNDO ME", [20, 20], null, null, null, drw);
        var undo = await new FeatureManagementService(S).UndoAsync(drw, 1);
        Assert.Equal(1, undo.Steps);
        var notes = await Drw.AnnotationListAsync(null, ["note"], 100, 0, drw);
        Assert.DoesNotContain(notes.Annotations, a => a.Name == note.Note);
    }

    [Fact]
    public async Task Check_finds_text_outside_the_frame_and_duplicates()
    {
        var (drw, front) = await FrontAsync("drwedit_check");
        var a = await Drw.DimensionAddAsync(front.Name, Bottom, null, "horizontal", null, drw);
        await Drw.DimensionAddAsync(front.Name, Bottom, null, "horizontal", [front.BoundsMm![0] + 10, front.BoundsMm[1] - 25], drw);
        await Drw.DimensionSetAsync(new DrwDimensionSetRequest { Doc = drw, Name = a.Dimension, TextAtMm = [415, 290] });
        var check = await Drw.CheckAsync(100, drw);
        Assert.Contains(check.Issues, i => i.Kind == "outside_frame");
        Assert.Contains(check.Issues, i => i.Kind == "duplicate");
    }

    [Fact]
    public async Task Locate_maps_model_to_sheet_and_finds_the_edge_under_a_point()
    {
        var (drw, front) = await FrontAsync("drwedit_locate");
        var toSheet = await Drw.LocateAsync(front.Name, null, [0, -30, 20], 3, drw);
        var p = toSheet.SheetMm!;
        var back = await Drw.LocateAsync(front.Name, p, null, 3, drw);
        Assert.Equal(0, back.ModelMm![0], 1e-3);
        Assert.Equal(-30, back.ModelMm[1], 1e-3);
        var e = back.Edges![0];
        Assert.Equal("line", e.Curve);
        Assert.Equal(100, e.LengthMm!.Value, 1e-3);
        await Fails(ErrorCodes.InvalidArgument, () => Drw.LocateAsync(null, null, [0, 0, 0], 3, drw));
    }

    [Fact]
    public async Task Batch_is_one_undo_step_and_a_failing_step_undoes_it()
    {
        var (drw, front) = await FrontAsync("drwedit_batch");
        var d1 = await Drw.DimensionAddAsync(front.Name, Bottom, null, "horizontal", null, drw);
        var d2 = await Drw.DimensionAddAsync(front.Name, Left, Right, "horizontal", [front.BoundsMm![0] + 20, front.BoundsMm[3] + 15], drw);
        var steps = new[]
        {
            new DrwBatchStep { Op = "dimension_set", Args = System.Text.Json.JsonSerializer.SerializeToElement(new { name = d1.Dimension, prefix = "Ø" }) },
            new DrwBatchStep { Op = "dimensions_arrange", Args = System.Text.Json.JsonSerializer.SerializeToElement(new { view = front.Name, side = "above", dimensions = new[] { d1.Dimension, d2.Dimension } }) },
        };
        var r = await Drw.BatchAsync(steps, false, drw);
        Assert.True(r.OneUndoStep);
        var bad = steps.Append(new DrwBatchStep { Op = "dimension_set", Args = System.Text.Json.JsonSerializer.SerializeToElement(new { name = "RD99@x", prefix = "x" }) }).ToList();
        await Fails(ErrorCodes.EntityNotFound, () => Drw.BatchAsync(bad, false, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.BatchAsync([new DrwBatchStep { Op = "note_add" }], false, drw));
    }

    [Fact]
    public async Task Hole_edit_changes_the_kind_in_place()
    {
        string part = await NewPartAsync("drwedit_hole");
        await new SketchService(S).StartAsync(part, new EntityRef { Name = "front" }, false);
        await new SketchService(S).AddAsync(part, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = part, DepthMm = 20 });
        var pf = new PartFeatureService(S);
        var hole = await pf.HoleAsync(new HoleRequest { Doc = part, Points = [[0, 0, 20]], Type = "clearance", Size = "M6", End = "through_all" });
        var edit = await pf.HoleEditAsync(new HoleEditRequest { Doc = part, Feature = hole.Feature.Name, Type = "countersink" });
        Assert.Equal(hole.Feature.Name, edit.Feature);
        Assert.True(edit.VolumeDeltaMm3 < 0);
        Assert.Contains("through_all", edit.After.EndCondition);
        await Fails(ErrorCodes.InvalidArgument, () => pf.HoleEditAsync(new HoleEditRequest { Doc = part, Feature = "Boss-Extrude1", Type = "drill" }));
    }

    [Fact]
    public async Task Standard_options_set_by_name_and_differ_from_another_drawing()
    {
        var (drw, _) = await FrontAsync("drwedit_std");
        var other = await Docs.NewAsync("drawing", "mmgs", null, $"drwedit_std2_{Guid.NewGuid().ToString("N")[..6]}");
        _drawings.Add(other.Document.Handle);
        var std = new StandardService(S);
        var r = await std.StandardAsync(new DocStandardRequest
        {
            Doc = drw, Set = new Dictionary<string, string> { ["leading_zeros"] = "remove", ["dimension_decimals"] = "3" }, CompareWith = other.Document.Handle,
        });
        Assert.Equal("remove", r.Options.Single(o => o.Name == "leading_zeros").Value);
        Assert.Equal("3", r.Options.Single(o => o.Name == "dimension_decimals").Value);
        Assert.Contains(r.Differences!, x => x.Name == "swDetailingLeadingZero");
        await Fails(ErrorCodes.InvalidArgument, () => std.StandardAsync(new DocStandardRequest { Doc = drw, Set = new Dictionary<string, string> { ["leading_zeros"] = "sometimes" } }));
    }

    [Fact]
    public async Task Title_block_lists_fields_and_writes_drawing_properties()
    {
        var (drw, _) = await FrontAsync("drwedit_title");
        var read = await Drw.TitleBlockAsync(new DrwTitleBlockRequest { Doc = drw });
        Assert.NotEmpty(read.Fields);
        var set = await Drw.TitleBlockAsync(new DrwTitleBlockRequest { Doc = drw, Values = new Dictionary<string, string> { ["DrawnBy"] = "MCP" } });
        Assert.Contains(set.Changed!, c => c.EndsWith("DrawnBy = MCP", StringComparison.Ordinal));
    }
}
