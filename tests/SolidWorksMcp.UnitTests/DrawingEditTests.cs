using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>The pure parts of the drawing-editing tools (DrawingEdit), doc_standard's options and the option-drift rule.</summary>
public class DrawingEditTests
{
    // ------------------------------------------------------------------ tolerances

    [Fact]
    public void Tolerances_map_to_swTolType_and_signed_deviations()
    {
        var b = DrawingEdit.Tolerance(new DimTolerance { Type = "bilateral", PlusMm = 0.1, MinusMm = 0.05 }, angular: false);
        Assert.Equal((2, -0.05, 0.1), (b.Code, b.MinMm, b.MaxMm));
        var s = DrawingEdit.Tolerance(new DimTolerance { Type = "symmetric", PlusMm = 0.02 }, false);
        Assert.Equal((4, -0.02, 0.02), (s.Code, s.MinMm, s.MaxMm));
        var l = DrawingEdit.Tolerance(new DimTolerance { Type = "limit", PlusMm = 0.2, MinusMm = -0.1 }, false); // +0.2/+0.1
        Assert.Equal((3, 0.1, 0.2), (l.Code, l.MinMm, l.MaxMm));
        Assert.Equal(0, DrawingEdit.Tolerance(new DimTolerance { Type = "none" }, false).Code);
        Assert.Equal(1, DrawingEdit.Tolerance(new DimTolerance { Type = "basic" }, false).Code);
        var fit = DrawingEdit.Tolerance(new DimTolerance { Hole = "h7", Shaft = "G6" }, false);
        Assert.Equal(("fit", 7, "H7", "g6"), (fit.Name, fit.Code, fit.Hole, fit.Shaft));
        // No type: ± from plus alone, bilateral from both.
        Assert.Equal("symmetric", DrawingEdit.Tolerance(new DimTolerance { PlusMm = 0.1 }, false).Name);
        Assert.Equal("bilateral", DrawingEdit.Tolerance(new DimTolerance { PlusMm = 0.1, MinusMm = 0 }, false).Name);
    }

    [Fact]
    public void Bad_tolerances_are_refused_before_any_com_call()
    {
        foreach (var t in new[]
                 {
                     new DimTolerance { Type = "wobbly" },
                     new DimTolerance { Type = "bilateral", PlusMm = 0.1 },
                     new DimTolerance { Type = "symmetric", PlusMm = 0 },
                     new DimTolerance { Type = "limit", PlusMm = -0.2, MinusMm = 0.1 },
                     new DimTolerance { Type = "none", PlusMm = 0.1 },
                     new DimTolerance { Type = "fit" },
                     new DimTolerance { Type = "fit", Hole = "H 7" },
                     new DimTolerance(),
                 })
            Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => DrawingEdit.Tolerance(t, false)).Code);
        Assert.Throws<SwException>(() => DrawingEdit.Tolerance(new DimTolerance { Hole = "H7" }, angular: true));
    }

    [Fact]
    public void Precision_is_0_to_8_places_or_the_document()
    {
        Assert.Null(DrawingEdit.Precision(null, "p"));
        Assert.Equal(-1, DrawingEdit.Precision(-1, "p"));
        Assert.Equal(3, DrawingEdit.Precision(3, "p"));
        Assert.Throws<SwException>(() => DrawingEdit.Precision(9, "p"));
        Assert.Throws<SwException>(() => DrawingEdit.Precision(-2, "p"));
    }

    // ------------------------------------------------------------------ text

    [Theory]
    [InlineData("A\\nB", "A\r\nB")]                    // the escape agents send for a line break
    [InlineData("A\nB", "A\r\nB")]
    [InlineData("A\r\nB", "A\r\nB")]
    [InlineData("A\\r\\nB", "A\r\nB")]
    [InlineData("C:\\\\new", "C:\\new")]               // \\n stays a literal backslash-n
    [InlineData("NOTES:\\n1. DEBURR\\n2. BREAK EDGES", "NOTES:\r\n1. DEBURR\r\n2. BREAK EDGES")]
    [InlineData("Ø10\\", "Ø10\\")]
    public void Note_text_turns_escaped_newlines_into_line_breaks(string input, string api) => Assert.Equal(api, DrawingEdit.NoteText(input));

    [Fact]
    public void Property_links_are_found_and_written()
    {
        var links = DrawingEdit.Links("$PRPSHEET:\"Description\" / $PRP:\"DrawnBy\" $PRPSHEET: \"Material\"");
        Assert.Equal([("model", "Description"), ("drawing", "DrawnBy"), ("model", "Material")], links);
        Assert.Equal("$PRP:\"DrawnBy\"", DrawingEdit.LinkText("drawing", "DrawnBy"));
        Assert.Equal("$PRPSHEET:\"Description\"", DrawingEdit.LinkText("model", " Description "));
        Assert.Throws<SwException>(() => DrawingEdit.LinkText("view", "X"));
        Assert.Throws<SwException>(() => DrawingEdit.LinkText("drawing", "a\"b"));
        Assert.True(DrawingEdit.HasLinks("$PRP:\"x\""));
        Assert.False(DrawingEdit.HasLinks("plain"));
    }

    [Fact]
    public void Names_match_by_annotation_dimension_or_view_qualified()
    {
        Assert.True(DrawingEdit.NameMatches("DetailItem3", "DetailItem3", null, "Drawing View1"));
        Assert.True(DrawingEdit.NameMatches("rd1@drawing view1", "DetailItem9", "RD1@Drawing View1", "Drawing View1"));
        Assert.True(DrawingEdit.NameMatches("D1@Drawing View2", "x", "D1@Sketch1@block.Part", "Drawing View2"));
        Assert.True(DrawingEdit.NameMatches("D1@Sketch1@block.Part", "x", "D1@Sketch1@block.Part", "Drawing View2"));
        Assert.False(DrawingEdit.NameMatches("D1", "x", "D1@Sketch1@block.Part", "Drawing View2")); // a bare model name is ambiguous
        Assert.True(DrawingEdit.NameMatches("RD1", "x", "RD1@Drawing View1", "Drawing View1"));
        Assert.False(DrawingEdit.NameMatches("RD1@Drawing View2", "x", "RD1@Drawing View1", "Drawing View1"));
        Assert.False(DrawingEdit.NameMatches(" ", "", null, "v"));
    }

    // ------------------------------------------------------------------ extents and the sheet check

    [Fact]
    public void Extents_and_text_boxes()
    {
        var e = DrawingEdit.Extent([(0.01, 0.02), (0.03, 0.005)], padMm: 1)!.Value;
        Assert.Equal(new SheetRect(9, 4, 31, 21), e);
        Assert.Null(DrawingEdit.Extent([]));
        Assert.Equal(new SheetRect(10, 20, 10 + 3 * 3.5 * 0.7, 23.5), DrawingEdit.TextBox(10, 20, 3.5, 3));
        var up = DrawingEdit.TextBox(0, 0, 2, 5, Math.PI / 2); // turned 90°: grows up and to the left
        Assert.True(up.H > up.W && up.X1 <= 1e-9 && up.Y0 >= -1e-9);
    }

    [Fact]
    public void Sheet_check_finds_overlaps_frame_dangling_duplicates_and_missing_diameter()
    {
        var frame = new SheetRect(10, 10, 410, 287);
        var items = new List<CheckItem>
        {
            new("RD1@Drawing View1", "dimension", "Drawing View1", new SheetRect(100, 100, 120, 105), false, 2, 20.0, null, "linear", false),
            new("RD2@Drawing View1", "dimension", "Drawing View1", new SheetRect(110, 102, 130, 107), false, 2, 20.0, null, "linear", false), // overlaps, duplicate
            new("RD3@Drawing View1", "dimension", "Drawing View1", new SheetRect(405, 200, 420, 205), false, 2, 12.0, "Ø", "linear", false), // outside
            new("RD4@Drawing View1", "dimension", "Drawing View1", new SheetRect(200, 200, 210, 205), true, 1, 8.0, null, "radial", false),  // dangling
            new("DetailItem7", "balloon", "Drawing View1", new SheetRect(300, 50, 306, 56), false, 0, null, null, null, false),             // unattached
            new("DetailItem8", "note", "Sheet1", new SheetRect(300, 60, 320, 64), false, 0, null, null, null, true),                         // a free note is fine
            new("RD5@Drawing View2", "dimension", "Drawing View2", new SheetRect(50, 250, 60, 255), false, 2, 12.0, null, "horizontal", false), // 12 = Ø12 cylinder
        };
        var issues = DrawingEdit.Check(items, frame, [12.0]);
        Assert.Contains(issues, i => i is { Kind: "overlap", Item: "RD1@Drawing View1", Other: "RD2@Drawing View1" });
        Assert.Contains(issues, i => i is { Kind: "duplicate", Item: "RD2@Drawing View1", Other: "RD1@Drawing View1" });
        Assert.Contains(issues, i => i is { Kind: "outside_frame", Item: "RD3@Drawing View1" });
        Assert.Contains(issues, i => i is { Kind: "dangling", Item: "RD4@Drawing View1" });
        Assert.Contains(issues, i => i is { Kind: "unattached", Item: "DetailItem7" });
        Assert.Contains(issues, i => i is { Kind: "missing_diameter_symbol", Item: "RD5@Drawing View2" });
        Assert.DoesNotContain(issues, i => i.Item == "DetailItem8");
        Assert.DoesNotContain(issues, i => i is { Kind: "missing_diameter_symbol", Item: "RD3@Drawing View1" }); // has Ø
        Assert.DoesNotContain(issues, i => i is { Kind: "duplicate", Item: "RD5@Drawing View2" });              // another view
    }

    [Fact]
    public void Touching_boxes_are_no_overlap()
    {
        var items = new List<CheckItem>
        {
            new("A", "note", "S", new SheetRect(0, 0, 10, 10), false, 0, null, null, null, true),
            new("B", "note", "S", new SheetRect(10, 0, 20, 10), false, 0, null, null, null, true),
            new("C", "note", "S", new SheetRect(19.9, 9.9, 30, 20), false, 0, null, null, null, true), // 0.1 mm corner
        };
        Assert.DoesNotContain(DrawingEdit.Check(items, new SheetRect(-100, -100, 100, 100), []), i => i.Kind == "overlap");
    }

    // ------------------------------------------------------------------ stacking, clamping, view moves

    [Fact]
    public void Stacked_dimensions_get_rows_with_the_smallest_value_nearest()
    {
        var outline = new SheetRect(100, 100, 200, 160);
        var slots = DrawingEdit.Stack([(150, 170, 80.0), (130, 175, 20.0), (170, 172, 50.0)], outline, "above", 10, 7);
        Assert.Equal([(0, 150.0, 184.0), (1, 130.0, 170.0), (2, 170.0, 177.0)], slots.Select(s => (s.Index, s.X, s.Y)));
        var left = DrawingEdit.Stack([(90, 130, 5.0)], outline, "left", 8, 7);
        Assert.Equal((92.0, 130.0), (left[0].X, left[0].Y));
        Assert.Throws<SwException>(() => DrawingEdit.Stack([], outline, "diagonal", 10, 7));
        Assert.Throws<SwException>(() => DrawingEdit.Stack([], outline, "above", 10, 0.5));
    }

    [Fact]
    public void Clamp_keeps_a_text_box_inside_the_frame()
    {
        var frame = new SheetRect(10, 10, 410, 287);
        Assert.Equal((200.0, 283.0, true), DrawingEdit.Clamp(200, 295, 20, 8, frame));
        Assert.Equal((20.0, 14.0, true), DrawingEdit.Clamp(0, 0, 20, 8, frame));
        Assert.Equal((100.0, 100.0, false), DrawingEdit.Clamp(100, 100, 20, 8, frame));
    }

    [Fact]
    public void Outline_correction_moves_centre_or_corner()
    {
        var now = new SheetRect(100, 50, 140, 90);
        Assert.Equal((80.0, 10.0), DrawingEdit.OutlineCorrection(now, 200, 80, null));
        Assert.Equal((0.0, 10.0), DrawingEdit.OutlineCorrection(now, null, 80, null));
        Assert.Equal((-90.0, -40.0), DrawingEdit.OutlineCorrection(now, null, null, new SheetRect(10, 10, 60, 60)));
    }

    // ------------------------------------------------------------------ doc_standard options

    [Fact]
    public void Named_options_convert_both_ways()
    {
        var lz = DraftingOptions.Find("leading_zeros", _ => null);
        Assert.Equal((PrefKind.Integer, 265, "3"), (lz.Kind, lz.Id, lz.ToApi("remove")));
        Assert.Equal("remove", lz.FromApi("3"));
        var dec = DraftingOptions.Find("Dimension-Decimals", _ => null);
        Assert.Equal("2", dec.ToApi("2"));
        Assert.Throws<SwException>(() => dec.ToApi("9"));
        var arrow = DraftingOptions.Find("arrow_length_mm", _ => null);
        Assert.Equal(0.003, double.Parse(arrow.ToApi("3"), System.Globalization.CultureInfo.InvariantCulture), 12);
        Assert.Equal("3", arrow.FromApi("0.003"));
        var raw = DraftingOptions.Find("integer:24", _ => null);
        Assert.Equal((PrefKind.Integer, 24), (raw.Kind, raw.Id));
        var byEnum = DraftingOptions.Find("swDetailingLinearDimPrecision", n => n == "swDetailingLinearDimPrecision" ? (PrefKind.Integer, 24) : null);
        Assert.Equal(24, byEnum.Id);
        Assert.Throws<SwException>(() => DraftingOptions.Find("nonsense", _ => null));
        Assert.Throws<SwException>(() => DraftingOptions.Find("dimension_arrow", _ => null).ToApi("spiky"));
    }

    [Fact]
    public void Option_diff_lists_only_differences()
    {
        var a = new Dictionary<string, string> { ["x"] = "1", ["y"] = "0.0035", ["z"] = "True", ["only"] = "1" };
        var b = new Dictionary<string, string> { ["x"] = "2", ["y"] = "0.0035000000001", ["z"] = "True" };
        Assert.Equal([("x", "1", "2")], DraftingOptions.Diff(a, b));
    }

    // ------------------------------------------------------------------ system options SOLIDWORKS changes by itself

    [Fact]
    public void Stl_deviation_under_a_preset_quality_is_derived_else_restored()
    {
        var before = new Dictionary<(PrefKind, int), string>
        {
            [(PrefKind.Integer, SystemPrefIds.StlQuality)] = "2", [(PrefKind.Double, SystemPrefIds.StlDeviation)] = "0.0001",
        };
        var drifted = new List<(PrefKind, int, string)> { (PrefKind.Double, SystemPrefIds.StlDeviation, "0.0001"), (PrefKind.Toggle, 10, "True") };
        var plan = PrefDrift.Plan(drifted, (k, p) => k == PrefKind.Integer && p == SystemPrefIds.StlQuality ? "2" : null, before);
        Assert.Equal(DriftAction.Derived, plan.Single(x => x.Pref == SystemPrefIds.StlDeviation).Action);
        Assert.Equal(DriftAction.Restore, plan.Single(x => x.Pref == 10).Action);
        // Custom quality: the deviation is the user's own value, so it goes back.
        before[(PrefKind.Integer, SystemPrefIds.StlQuality)] = "3";
        plan = PrefDrift.Plan(drifted, (k, p) => "3", before);
        Assert.All(plan, x => Assert.Equal(DriftAction.Restore, x.Action));
        Assert.Equal(SystemPrefIds.StlDeviation, plan[0].Pref); // the coupled group first, in its order
    }

    // ------------------------------------------------------------------ mate side

    [Fact]
    public void Mate_side_is_the_sign_along_as_normal()
    {
        var a = new AsmGeo('p', new Vec3(0, 0, 0), new Vec3(0, 0, 1), 0, "a");
        Assert.Equal(1, AsmMateIntent.Side(a, new AsmGeo('p', new Vec3(5, 3, 0.01), new Vec3(0, 0, -1), 0, "b")));
        Assert.Equal(-1, AsmMateIntent.Side(a, new AsmGeo('p', new Vec3(0, 0, -0.01), new Vec3(0, 0, 1), 0, "b")));
        Assert.Equal(0, AsmMateIntent.Side(a, new AsmGeo('p', new Vec3(0, 0, 0.01), new Vec3(1, 0, 0), 0, "b"))); // not parallel
        Assert.Equal(0, AsmMateIntent.Side(a, new AsmGeo('a', new Vec3(0, 0, 0.01), new Vec3(0, 0, 1), 0.002, "b")));
    }
}
