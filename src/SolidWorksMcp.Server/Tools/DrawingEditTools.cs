using System.ComponentModel;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>
/// Editing what is already on a drawing (finishing a partly done drawing is mostly editing): list annotations, change
/// dimensions, notes and other annotations, and delete annotations, views, detail circles, section lines and view
/// sketch segments by name. Names come from drw_annotation_list and drw_info.
/// </summary>
[McpServerToolType, Toolset("drawing")]
public sealed class DrawingEditTools(SwSession session)
{
    private const string DocDesc = "Drawing (handle, title or path); default: the target document.";
    private const string NameDesc = "Annotation name from drw_annotation_list, e.g. \"RD1@Drawing View1\", \"D1@Sketch1@block.Part\" or \"DetailItem3\" " +
                                    "(name@view picks one when several views share a name).";

    private readonly DrawingService _svc = new(session);

    [McpServerTool(Name = "drw_annotation_list", Title = "List drawing annotations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List / read the annotations of the current sheet, per view: type, name, text (shown and raw, e.g. $PRPSHEET links), " +
                 "position and approximate box (sheet mm), leader end points, what it is attached to (and where, in model mm), dangling " +
                 "state, and for dimensions the value, prefix/suffix, tolerance, precision, parentheses and whether it is a reference " +
                 "dimension; tables give rows and columns. The names are what drw_dimension_set, drw_annotation_set and drw_delete take.")]
    public async Task<string> DrwAnnotationList(
        [Description("Views to list (the sheet's own name for annotations on the sheet); default: all views of the current sheet.")] string[]? views = null,
        [Description("Only these types: dimension, note, balloon, datum, gtol, surface_finish, center_mark, centerline, table, weld, …")] string[]? types = null,
        [Description("Maximum annotations to return.")] int limit = 100,
        [Description("Skip this many first.")] int offset = 0,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.AnnotationListAsync(views, types, limit, offset, doc, cancellationToken));

    [McpServerTool(Name = "drw_dimension_set", Title = "Edit drawing dimension", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Edit an existing dimension in a drawing: prefix / suffix text (Ø, STROKE, 4X), text above / below, tolerance " +
                 "(none, basic, bilateral, limit, symmetric, min, max, or an ISO fit like H7/g6), decimal places of the value and of " +
                 "the tolerance, parentheses (a reference dimension's look), text height and position. Every setting is read back; " +
                 "if one doesn't take, all are put back. A tolerance on a model dimension changes the model too (it must be the server's).")]
    public async Task<string> DrwDimensionSet(
        [Description(NameDesc)] string name,
        [Description("Text before the value, e.g. \"Ø\" or \"4X \"; \"\" clears it.")] string? prefix = null,
        [Description("Text after the value, e.g. \" STROKE\"; \"\" clears it.")] string? suffix = null,
        [Description("Text above the dimension line.")] string? above = null,
        [Description("Text below the dimension line.")] string? below = null,
        [Description("{type: none|basic|bilateral|limit|symmetric|min|max|fit, plusMm, minusMm (a magnitude: 0.05 for −0.05), hole:'H7', shaft:'g6'}; angular dimensions in degrees.")] DimTolerance? tolerance = null,
        [Description("Decimal places of the value (0..8; -1 follows the document).")] int? precision = null,
        [Description("Decimal places of the tolerance (0..8; -1 follows the document).")] int? tolerancePrecision = null,
        [Description("Move the dimension text to [x,y] sheet mm.")] double[]? textAtMm = null,
        [Description("true shows the value in parentheses (reference dimension), false removes them.")] bool? parentheses = null,
        [Description("Text height in mm.")] double? heightMm = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.DimensionSetAsync(new DrwDimensionSetRequest
        {
            Doc = doc, Name = name, Prefix = prefix, Suffix = suffix, Above = above, Below = below, Tolerance = tolerance, Precision = precision,
            TolerancePrecision = tolerancePrecision, TextAtMm = textAtMm, Parentheses = parentheses, HeightMm = heightMm,
        }, cancellationToken));

    [McpServerTool(Name = "drw_annotation_set", Title = "Edit drawing annotation", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Edit an existing note or other annotation in a drawing: replace a note's text (line breaks or \\n; property links like " +
                 "$PRP:\"DrawnBy\" stay links), move any annotation or table, set the text height (notes, tables, symbols) or a note's " +
                 "angle. Read back; if one change doesn't take, all are put back.")]
    public async Task<string> DrwAnnotationSet(
        [Description(NameDesc)] string name,
        [Description("New note text (replaces the whole note).")] string? text = null,
        [Description("New position [x,y] sheet mm (a table's top-left corner).")] double[]? atMm = null,
        [Description("Text height in mm.")] double? heightMm = null,
        [Description("Note text angle in degrees.")] double? angleDeg = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.AnnotationSetAsync(new DrwAnnotationSetRequest
        {
            Doc = doc, Name = name, Text = text, AtMm = atMm, HeightMm = heightMm, AngleDeg = angleDeg,
        }, cancellationToken));

    [McpServerTool(Name = "drw_delete", Title = "Delete drawing items", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Delete by name from the current sheet: annotations (dimensions, notes, balloons, tables, …), views, detail circles " +
                 "(\"Detail Circle B\"), section lines (\"Section Line A\") and sketch segments of a view's sketch (\"Line1@Drawing View1\"). " +
                 "dryRun shows what the names resolve to. Verified: each item is gone afterwards; doc_undo puts them back.")]
    public async Task<string> DrwDelete(
        [Description("Names from drw_annotation_list / drw_info.")] string[] names,
        [Description("Resolve the names and report, delete nothing.")] bool dryRun = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.DeleteAsync(names, dryRun, doc, cancellationToken));

    [McpServerTool(Name = "drw_title_block", Title = "Title block", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read and fill the title block (sheet format) of the current sheet: its fields with shown and raw text and the " +
                 "properties they link ($PRP: drawing, $PRPSHEET: the sheet's model). values writes each property where the title block " +
                 "links it (e.g. DrawnBy, Description); links re-links a field to a property; texts sets a field's literal text. Without " +
                 "arguments: read only. Each write is read back; fields left empty are listed.")]
    public async Task<string> DrwTitleBlock(
        [Description("Property → value, e.g. {\"DrawnBy\":\"JS\",\"Description\":\"Bracket\"}.")] Dictionary<string, string>? values = null,
        [Description("[{field:'Note12', property:'Description', scope:'model'|'drawing'}]: show that property in that field.")] DrwFieldLink[]? links = null,
        [Description("Field → literal text, e.g. {\"Note7\":\"A3\"}.")] Dictionary<string, string>? texts = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.TitleBlockAsync(new DrwTitleBlockRequest { Doc = doc, Values = values, Links = links, Texts = texts }, cancellationToken));

    [McpServerTool(Name = "drw_hatch", Title = "Section hatches", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the hatches of a section view per component (pattern, angle, scale, material hatch), and set pattern, angleDeg, " +
                 "scale or material (follow the material's hatch) for all of them or for some components. Without settings: read only. " +
                 "Each change is read back.")]
    public async Task<string> DrwHatch(
        [Description("Section view name.")] string view,
        [Description("Only these components' hatches, e.g. [\"bolt-1\"]; default: all.")] string[]? components = null,
        [Description("Hatch pattern, e.g. \"ANSI31\".")] string? pattern = null,
        [Description("Hatch angle in degrees.")] double? angleDeg = null,
        [Description("Hatch scale.")] double? scale = null,
        [Description("true: follow the material's hatch; false: use pattern/angle/scale.")] bool? material = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.HatchAsync(new DrwHatchRequest
        {
            Doc = doc, View = view, Components = components, Pattern = pattern, AngleDeg = angleDeg, Scale = scale, Material = material,
        }, cancellationToken));

    [McpServerTool(Name = "drw_table_set", Title = "Edit table or BOM", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Edit a table (BOM, hole, revision, general) by name: type into cells, reorder rows or columns, insert a column, link " +
                 "a BOM column to a custom property, set column widths, row height and text height, and move it. Rows and columns count " +
                 "from 0 with the header. Each change is read back; returns the cells as they read now.")]
    public async Task<string> DrwTableSet(
        [Description("Table name from drw_annotation_list (type table).")] string table,
        [Description("[{row, column, text}] to type in.")] DrwCell[]? cells = null,
        [Description("Column index → width mm, e.g. {\"1\":40}.")] Dictionary<string, double>? columnWidthsMm = null,
        [Description("Height of every row in mm.")] double? rowHeightMm = null,
        [Description("Text height in mm.")] double? textHeightMm = null,
        [Description("[from, to]: move a row.")] int[]? moveRow = null,
        [Description("[from, to]: move a column.")] int[]? moveColumn = null,
        [Description("Insert a column with this title.")] string? insertColumn = null,
        [Description("Where to insert it (default: last).")] int? insertColumnAt = null,
        [Description("BOM: [column, property], e.g. [\"3\",\"Material\"].")] string[]? columnProperty = null,
        [Description("Move the table's top-left corner to [x,y] sheet mm.")] double[]? atMm = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.TableSetAsync(new DrwTableSetRequest
        {
            Doc = doc, Table = table, Cells = cells, ColumnWidthsMm = columnWidthsMm, RowHeightMm = rowHeightMm, TextHeightMm = textHeightMm,
            MoveRow = moveRow, MoveColumn = moveColumn, InsertColumn = insertColumn, InsertColumnAt = insertColumnAt, ColumnProperty = columnProperty, AtMm = atMm,
        }, cancellationToken));
}
