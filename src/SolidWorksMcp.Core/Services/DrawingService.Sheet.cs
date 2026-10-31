using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== DTOs: title block, hatches, tables

/// <summary>drw_title_block: values for properties the title block shows, and fields to re-link or rewrite.</summary>
public sealed record DrwTitleBlockRequest
{
    public string? Doc { get; init; }
    /// <summary>Property name → value; each goes where the title block links it ($PRP: the drawing, $PRPSHEET: the sheet's model).</summary>
    public IReadOnlyDictionary<string, string>? Values { get; init; }
    /// <summary>Title-block note name → property it should show ($PRPSHEET:"name" or $PRP:"name").</summary>
    public IReadOnlyList<DrwFieldLink>? Links { get; init; }
    /// <summary>Title-block note name → literal text.</summary>
    public IReadOnlyDictionary<string, string>? Texts { get; init; }
}

/// <summary>A title-block field to link to a property.</summary>
public sealed record DrwFieldLink
{
    /// <summary>The note's name (drw_title_block lists them).</summary>
    public string? Field { get; init; }
    public string? Property { get; init; }
    /// <summary>drawing ($PRP, the drawing's property) | model ($PRPSHEET, the model shown on the sheet).</summary>
    public string? Scope { get; init; }
}

/// <summary>One text of the sheet format (title block).</summary>
public sealed class DrwField
{
    public required string Field { get; init; }
    /// <summary>As shown.</summary>
    public string? Text { get; init; }
    /// <summary>As stored, e.g. $PRPSHEET:"Description".</summary>
    public string? Raw { get; init; }
    /// <summary>The properties it shows: "drawing:DrawnBy", "model:Description".</summary>
    public IReadOnlyList<string>? Links { get; init; }
    public double[]? AtMm { get; init; }
}

public sealed class DrwTitleBlockResult : ToolResult
{
    public required string Sheet { get; init; }
    public required IReadOnlyList<DrwField> Fields { get; init; }
    /// <summary>What was written: "drawing:DrawnBy = JS", "field Note5 → $PRP:\"DrawnBy\"".</summary>
    public IReadOnlyList<string>? Changed { get; init; }
    /// <summary>Linked properties that are empty (the field shows nothing).</summary>
    public IReadOnlyList<string>? Empty { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>A section's hatch on one face.</summary>
public sealed class DrwHatchInfo
{
    public int Index { get; init; }
    /// <summary>The component of the hatched face (assembly views).</summary>
    public string? Component { get; init; }
    public string? Pattern { get; init; }
    public double AngleDeg { get; init; }
    public double Scale { get; init; }
    /// <summary>The hatch follows the material's crosshatch.</summary>
    public bool Material { get; init; }
    public bool? Solid { get; init; }
}

public sealed record DrwHatchRequest
{
    public string? Doc { get; init; }
    public string? View { get; init; }
    /// <summary>Only these components' hatches (default: all of the view).</summary>
    public IReadOnlyList<string>? Components { get; init; }
    public string? Pattern { get; init; }
    public double? AngleDeg { get; init; }
    public double? Scale { get; init; }
    /// <summary>true: follow the material's hatch; false: use pattern/angle/scale.</summary>
    public bool? Material { get; init; }
}

public sealed class DrwHatchResult : ToolResult
{
    public required string View { get; init; }
    public required IReadOnlyList<DrwHatchInfo> Hatches { get; init; }
    public int Changed { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>One cell to type into.</summary>
public sealed record DrwCell
{
    public int Row { get; init; }
    public int Column { get; init; }
    public string? Text { get; init; }
}

/// <summary>drw_table_set: edits of a table (BOM, hole, revision or general); rows and columns count from 0, headers included.</summary>
public sealed record DrwTableSetRequest
{
    public string? Doc { get; init; }
    public string? Table { get; init; }
    public IReadOnlyList<DrwCell>? Cells { get; init; }
    /// <summary>Column index → width mm.</summary>
    public IReadOnlyDictionary<string, double>? ColumnWidthsMm { get; init; }
    public double? RowHeightMm { get; init; }
    public double? TextHeightMm { get; init; }
    /// <summary>[from, to]: move a row (data rows; the header stays).</summary>
    public int[]? MoveRow { get; init; }
    /// <summary>[from, to]: move a column.</summary>
    public int[]? MoveColumn { get; init; }
    /// <summary>Insert a column with this title (at <see cref="InsertColumnAt"/>, default: last).</summary>
    public string? InsertColumn { get; init; }
    public int? InsertColumnAt { get; init; }
    /// <summary>BOM: link a column to a custom property: [column index (as text), property].</summary>
    public string[]? ColumnProperty { get; init; }
    /// <summary>Move the table: its top-left corner [x, y] sheet mm.</summary>
    public double[]? AtMm { get; init; }
}

public sealed class DrwTableSetResult : ToolResult
{
    public required string Table { get; init; }
    public int Rows { get; init; }
    public int Columns { get; init; }
    public required IReadOnlyList<string> Changed { get; init; }
    /// <summary>The table's text as it reads now (up to 50 rows).</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Cells { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed partial class DrawingService
{
    // ================================================================== drw_title_block

    public Task<DrwTitleBlockResult> TitleBlockAsync(DrwTitleBlockRequest q, CancellationToken ct = default)
    {
        foreach (var l in q.Links ?? [])
        {
            if (string.IsNullOrWhiteSpace(l.Field) || string.IsNullOrWhiteSpace(l.Property))
                throw SwException.InvalidArgument("Each link needs 'field' (a title-block note) and 'property'.");
            DrawingEdit.LinkText(l.Scope ?? "model", l.Property);
        }
        foreach (var (k, v) in q.Values ?? new Dictionary<string, string>())
            if (string.IsNullOrWhiteSpace(k) || v is null || v.Length > 1000) throw SwException.InvalidArgument($"values: '{k}' needs a value of at most 1000 characters.");
        bool writes = q.Values is { Count: > 0 } || q.Links is { Count: > 0 } || q.Texts is { Count: > 0 };
        return session.RunAsync("drw_title_block", ctx => TitleBlock(ctx, q), writes ? RunOptions.Write(q.Doc) : RunOptions.Read(q.Doc), ct);
    }

    private static DrwTitleBlockResult TitleBlock(SwContext ctx, DrwTitleBlockRequest q)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var sheet = drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, "The drawing has no current sheet.");
        var changed = new List<string>();
        var checks = new List<string>();
        var fields = FormatNotes(drw);
        if (fields.Count == 0 && sheet.GetTemplateName() is not { Length: > 0 })
            ctx.Warn("The sheet has no sheet format (title block); set one with drw_sheet_setup format.");

        // 1. Field links and literal texts: edited inside the sheet format.
        var edits = new List<(string Field, string Text, string What)>();
        foreach (var l in q.Links ?? [])
            edits.Add((l.Field!.Trim(), DrawingEdit.LinkText(l.Scope ?? "model", l.Property!), $"field {l.Field} → {DrawingEdit.LinkText(l.Scope ?? "model", l.Property!)}"));
        foreach (var (f, t) in q.Texts ?? new Dictionary<string, string>())
            edits.Add((f.Trim(), DrawingEdit.NoteText(t), $"field {f} → '{Clip(t, 40)}'"));
        if (edits.Count > 0)
        {
            foreach (var e in edits)
                if (!fields.Any(x => string.Equals(x.Name, e.Field, StringComparison.OrdinalIgnoreCase)))
                    throw new SwException(ErrorCodes.EntityNotFound, $"The title block has no field '{e.Field}'.",
                        $"Fields: {string.Join(", ", fields.Take(20).Select(x => x.Name))} (drw_title_block without arguments lists them with their text).");
            drw.EditTemplate();
            try
            {
                foreach (var (field, text, what) in edits)
                {
                    var n = FormatNotes(drw, editing: true).First(x => string.Equals(x.Name, field, StringComparison.OrdinalIgnoreCase)).Note;
                    if (!n.SetText(text)) ctx.Warn($"SetText on '{field}' returned false.");
                    string raw = DrawingEdit.Plain(n.PropertyLinkedText is { Length: > 0 } r ? r : n.GetText());
                    if (raw != DrawingEdit.Plain(text))
                        throw new SwException(ErrorCodes.VerifyFailed, $"Title-block field '{field}' reads '{Clip(raw, 60)}' after the edit (earlier fields of this call stay changed).");
                    changed.Add(what);
                }
            }
            finally { drw.EditSheet(); }
            checks.Add($"{edits.Count} field(s) read back");
        }

        // 2. Property values, written where the title block links them.
        if (q.Values is { Count: > 0 })
        {
            var links = FormatNotes(drw).SelectMany(f => DrawingEdit.Links(f.Raw)).ToList();
            IModelDoc2? model = null;
            foreach (var (prop, value) in q.Values)
            {
                var scopes = links.Where(l => string.Equals(l.Name, prop, StringComparison.OrdinalIgnoreCase)).Select(l => l.Scope).Distinct().ToList();
                if (scopes.Count == 0)
                {
                    scopes = ["drawing"];
                    ctx.Warn($"No title-block field shows '{prop}'; it was written as a drawing property (link a field with 'links').");
                }
                foreach (var scope in scopes)
                {
                    IModelDoc2 target = d;
                    if (scope != "drawing")
                    {
                        model ??= SheetModel(drw) ?? throw new SwException(ErrorCodes.NotSupported, $"'{prop}' is shown from the sheet's model, but the sheet has no view of a model yet.");
                        var e = ctx.Documents.Track(model);
                        if (e.Class is not (DocClass.Owned or DocClass.Adopted))
                            throw new SwException(ErrorCodes.NotOwned, $"'{prop}' is a property of the model '{e.Title}', which this server didn't create or open.",
                                $"doc_activate {{doc:'{e.Title}', allowEdit:true}} first, or relink the field to a drawing property (links scope 'drawing').");
                        target = model;
                    }
                    var mgr = target.Extension.CustomPropertyManager[""];
                    mgr.Add3(prop, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    mgr.Get6(prop, false, out string raw, out _, out _, out _);
                    if (raw != value) throw new SwException(ErrorCodes.VerifyFailed, $"Property '{prop}' of '{ctx.Title(target)}' reads '{raw}', not '{value}'.");
                    changed.Add($"{scope}:{prop} = {Clip(value, 40)}");
                }
            }
            d.ForceRebuild3(false);
            checks.Add($"{q.Values.Count} propert(ies) written and read back");
        }

        fields = FormatNotes(drw);
        var empty = new List<string>();
        var result = fields.Select(f =>
        {
            var links = DrawingEdit.Links(f.Raw).Select(l => $"{l.Scope}:{l.Name}").ToList();
            if (links.Count > 0 && string.IsNullOrWhiteSpace(f.Text)) empty.AddRange(links);
            return new DrwField
            {
                Field = f.Name, Text = string.IsNullOrEmpty(f.Text) ? null : f.Text, Raw = f.Raw != f.Text && !string.IsNullOrEmpty(f.Raw) ? f.Raw : null,
                Links = links.Count > 0 ? links : null, AtMm = f.At,
            };
        }).ToList();
        return new DrwTitleBlockResult
        {
            Doc = ctx.Title(d), Sheet = sheet.GetName(), Fields = result, Changed = changed.Count > 0 ? changed : null,
            Empty = empty.Count > 0 ? empty.Distinct().ToList() : null, Checks = checks.Count > 0 ? checks : null,
        };
    }

    private sealed record FormatNote(string Name, INote Note, string Text, string Raw, double[]? At);

    /// <summary>
    /// The notes of the current sheet's sheet format (title block): the sheet's notes owned by the template, read while
    /// editing the format when they don't list otherwise.
    /// </summary>
    private static List<FormatNote> FormatNotes(IDrawingDoc drw, bool editing = false)
    {
        List<FormatNote> Read(bool onlyTemplate)
        {
            var list = new List<FormatNote>();
            var sv = drw.DrwSheetView();
            if (sv is null) return list;
            foreach (var n in sv.DrwNotes())
            {
                var an = n.DrwAnnotation();
                if (onlyTemplate && Safe(() => an?.OwnerType ?? -1, -1) != (int)swAnnotationOwner_e.swAnnotationOwner_DrawingTemplate) continue;
                var p = an is null ? [] : Safe(() => an.DrwPosition(), []);
                list.Add(new FormatNote(Safe(() => n.GetName(), "") ?? "", n, DrawingEdit.Plain(Safe(() => n.GetText(), "")),
                    DrawingEdit.Plain(Safe(() => n.PropertyLinkedText, "")), p.Length >= 2 ? [Math.Round(p[0] * 1000, 3), Math.Round(p[1] * 1000, 3)] : null));
            }
            return list;
        }
        if (editing) return Read(false);
        var mine = Read(true);
        if (mine.Count > 0) return mine;
        drw.EditTemplate();
        try { return Read(false); }
        finally { drw.EditSheet(); }
    }

    /// <summary>The model shown on the current sheet ($PRPSHEET's source: the first view's model).</summary>
    private static IModelDoc2? SheetModel(IDrawingDoc drw)
    {
        string? cur = drw.DrwCurrentSheet()?.GetName();
        return AllViews(drw).Where(v => v.Sheet == cur && v.View.Type != (int)swDrawingViewTypes_e.swDrawingSheet)
            .Select(v => Safe(() => v.View.ReferencedDocument, null)).FirstOrDefault(m => m is not null);
    }

    // ================================================================== drw_hatch

    public Task<DrwHatchResult> HatchAsync(DrwHatchRequest q, CancellationToken ct = default)
    {
        if (q.AngleDeg is double a && (!double.IsFinite(a) || Math.Abs(a) > 360)) throw SwException.InvalidArgument($"angleDeg must be -360..360 (got {a}).");
        if (q.Scale is double s && (!double.IsFinite(s) || s <= 0 || s > 1000)) throw SwException.InvalidArgument($"scale must be > 0 (got {s}).");
        if (q.Material == true && (q.Pattern is not null || q.AngleDeg is not null || q.Scale is not null))
            throw SwException.InvalidArgument("material:true takes the material's hatch; leave pattern, angleDeg and scale out (or pass material:false).");
        bool writes = q.Pattern is not null || q.AngleDeg is not null || q.Scale is not null || q.Material is not null;
        return session.RunAsync("drw_hatch", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var hit = FindView(ctx, drw, q.View, "view");
            var hatches = ComArgs.Objects<IFaceHatch>(Safe(() => (object?)hit.View.GetFaceHatches(), null));
            if (hatches.Count == 0)
                throw new SwException(ErrorCodes.EntityNotFound, $"'{hit.Name}' has no face hatches.", "Hatches belong to section views (drw_view_section) and broken-out sections.");
            string? Comp(IFaceHatch h) => Safe(() => ((object?)h.Face as IEntity)?.AsmComponentOf()?.Name2, null);
            var pick = hatches.Select((h, i) => (h, i, c: Comp(h))).ToList();
            if (q.Components is { Count: > 0 } cs)
            {
                foreach (var c in cs)
                    if (!pick.Any(p => Matches(p.c, c)))
                        throw new SwException(ErrorCodes.EntityNotFound, $"No hatch of component '{c}' in '{hit.Name}'.",
                            $"Hatched components: {string.Join(", ", pick.Select(p => p.c ?? "(part)").Distinct().Take(12))}.");
                pick = pick.Where(p => cs.Any(c => Matches(p.c, c))).ToList();
            }
            int changed = 0;
            var checks = new List<string>();
            if (writes && ctx.Access != DocAccess.Write) throw new SwException(ErrorCodes.Internal, "drw_hatch: a write needs write access.");
            foreach (var (h, i, c) in pick.Where(_ => writes))
            {
                if (q.Material is bool m) h.UseMaterialHatch = m;
                if (q.Pattern is not null) h.Pattern = q.Pattern;
                if (q.AngleDeg is double ang) h.Angle = Units.Rad(ang);
                if (q.Scale is double sc) h.Scale2 = sc;
                var problems = new List<string>();
                if (q.Material is bool mm && h.UseMaterialHatch != mm) problems.Add($"material reads {h.UseMaterialHatch}");
                if (q.Pattern is not null && !string.Equals(h.Pattern, q.Pattern, StringComparison.OrdinalIgnoreCase)) problems.Add($"pattern reads '{h.Pattern}'");
                if (q.AngleDeg is double a2 && Math.Abs(h.Angle - Units.Rad(a2)) > 1e-6) problems.Add($"angle reads {Units.Deg(h.Angle)}°");
                if (q.Scale is double s2 && Math.Abs(h.Scale2 - s2) > 1e-9 * Math.Max(1, s2)) problems.Add($"scale reads {h.Scale2}");
                if (problems.Count > 0)
                    throw new SwException(ErrorCodes.VerifyFailed, $"Hatch {i}{(c is null ? "" : $" ({c})")}: {string.Join("; ", problems)} ({changed} hatch(es) before it were changed).",
                        q.Pattern is not null ? "Pattern names are those of SOLIDWORKS' sldwks.ptn (e.g. ANSI31, ANSI32, ISO (Steel))." : null);
                changed++;
            }
            if (changed > 0)
            {
                d.GraphicsRedraw2();
                checks.Add($"{changed} hatch(es) set and read back");
            }
            var infos = pick.Select(p => new DrwHatchInfo
            {
                Index = p.i, Component = p.c, Pattern = Safe(() => p.h.Pattern, null), AngleDeg = Units.Deg(Safe(() => p.h.Angle, 0.0)),
                Scale = Math.Round(Safe(() => p.h.Scale2, 0.0), 6), Material = Safe(() => p.h.UseMaterialHatch, false),
                Solid = Safe(() => p.h.SolidFill, false) ? true : null,
            }).ToList();
            return new DrwHatchResult { Doc = ctx.Title(d), View = hit.Name, Hatches = infos, Changed = changed, Checks = checks.Count > 0 ? checks : null };
        }, writes ? RunOptions.Write(q.Doc) : RunOptions.Read(q.Doc), ct);
    }

    private static bool Matches(string? component, string query) =>
        component is not null && (string.Equals(component, query.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(component.Split('/').Last(), query.Trim(), StringComparison.OrdinalIgnoreCase));

    // ================================================================== drw_table_set

    public Task<DrwTableSetResult> TableSetAsync(DrwTableSetRequest q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q.Table)) throw SwException.InvalidArgument("'table' is required: the table's name from drw_annotation_list (type table).");
        foreach (var (k, w) in q.ColumnWidthsMm ?? new Dictionary<string, double>())
            if (!int.TryParse(k, out int ci) || ci < 0 || !double.IsFinite(w) || w < 1 || w > 500)
                throw SwException.InvalidArgument($"columnWidthsMm: '{k}' must be a column index with a width of 1..500 mm.");
        foreach (var (n, v) in new[] { ("rowHeightMm", q.RowHeightMm), ("textHeightMm", q.TextHeightMm) })
            if (v is double x && (!double.IsFinite(x) || x < 0.5 || x > 200)) throw SwException.InvalidArgument($"{n} must be 0.5..200 mm.");
        foreach (var (n, v) in new[] { ("moveRow", q.MoveRow), ("moveColumn", q.MoveColumn) })
            if (v is not null && (v.Length != 2 || v.Any(i => i < 0))) throw SwException.InvalidArgument($"{n} must be [from, to] (0-based).");
        if (q.ColumnProperty is not null && (q.ColumnProperty.Length != 2 || !int.TryParse(q.ColumnProperty[0], out _) || string.IsNullOrWhiteSpace(q.ColumnProperty[1])))
            throw SwException.InvalidArgument("columnProperty must be [column index, property name], e.g. [\"3\", \"Material\"].");
        if (q.AtMm is not null) Sheet2(q.AtMm, "atMm");
        if (q.Cells is { Count: > 500 }) throw SwException.InvalidArgument("At most 500 cells per call.");
        return session.RunAsync("drw_table_set", ctx => TableSet(ctx, q), RunOptions.Write(q.Doc), ct);
    }

    private static DrwTableSetResult TableSet(SwContext ctx, DrwTableSetRequest q)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var a = FindAnnotation(drw, q.Table, "table");
        var t = a.Table ?? throw SwException.InvalidArgument($"'{a.Name}' is a {a.Type}, not a table.");
        var changed = new List<string>();
        var checks = new List<string>();
        void Range(int i, int count, string what)
        {
            if (i < 0 || i >= count) throw SwException.InvalidArgument($"{what} {i} is outside the table (0..{count - 1}).");
        }
        if (q.InsertColumn is string title)
        {
            int cols = t.ColumnCount, at = q.InsertColumnAt ?? cols;
            if (at < 0 || at > cols) throw SwException.InvalidArgument($"insertColumnAt {at} is outside 0..{cols}.");
            bool ok = at == cols
                ? t.InsertColumn2((int)swTableItemInsertPosition_e.swTableItemInsertPosition_Last, 0, title, (int)swInsertTableColumnWidthStyle_e.swInsertColumn_DefaultWidth)
                : t.InsertColumn2((int)swTableItemInsertPosition_e.swTableItemInsertPosition_Before, at, title, (int)swInsertTableColumnWidthStyle_e.swInsertColumn_DefaultWidth);
            if (!ok || t.ColumnCount != cols + 1) throw new SwException(ErrorCodes.VerifyFailed, $"The column '{title}' was not inserted ({t.ColumnCount} columns).");
            changed.Add($"inserted column '{title}' at {at}");
        }
        if (q.ColumnProperty is { } cp)
        {
            int col = int.Parse(cp[0]);
            Range(col, t.ColumnCount, "column");
            var bom = (object)t as IBomTableAnnotation ?? throw SwException.InvalidArgument($"'{a.Name}' is not a BOM; only BOM columns link to custom properties.");
            t.SetColumnType(col, (int)swTableColumnTypes_e.swBomTableColumnType_CustomProperty);
            if (!bom.SetColumnCustomProperty(col, cp[1].Trim()) || !string.Equals(bom.GetColumnCustomProperty(col), cp[1].Trim(), StringComparison.OrdinalIgnoreCase))
                throw new SwException(ErrorCodes.VerifyFailed, $"Column {col} does not read property '{cp[1]}' (it reads '{bom.GetColumnCustomProperty(col)}').");
            changed.Add($"column {col} → property '{cp[1].Trim()}'");
        }
        if (q.MoveColumn is { } mc)
        {
            Range(mc[0], t.ColumnCount, "moveColumn from"); Range(mc[1], t.ColumnCount, "moveColumn to");
            string head = t.Text[0, mc[0]] ?? "";
            if (!t.MoveColumn(mc[0], mc[1] > mc[0] ? (int)swTableItemInsertPosition_e.swTableItemInsertPosition_After : (int)swTableItemInsertPosition_e.swTableItemInsertPosition_Before, mc[1]))
                throw new SwException(ErrorCodes.FeatureFailed, $"MoveColumn {mc[0]} → {mc[1]} returned false.");
            if ((t.Text[0, mc[1]] ?? "") != head) ctx.Warn($"Column {mc[1]} reads '{t.Text[0, mc[1]]}' after the move, not '{head}'.");
            changed.Add($"column {mc[0]} → {mc[1]}");
        }
        if (q.MoveRow is { } mr)
        {
            Range(mr[0], t.RowCount, "moveRow from"); Range(mr[1], t.RowCount, "moveRow to");
            string first = string.Join("|", Enumerable.Range(0, t.ColumnCount).Select(c => t.DisplayedText[mr[0], c] ?? ""));
            if (!t.MoveRow(mr[0], mr[1] > mr[0] ? (int)swTableItemInsertPosition_e.swTableItemInsertPosition_After : (int)swTableItemInsertPosition_e.swTableItemInsertPosition_Before, mr[1]))
                throw new SwException(ErrorCodes.FeatureFailed, $"MoveRow {mr[0]} → {mr[1]} returned false (header rows and some BOM rows don't move).");
            string now = string.Join("|", Enumerable.Range(0, t.ColumnCount).Select(c => t.DisplayedText[mr[1], c] ?? ""));
            if (now != first) ctx.Warn($"Row {mr[1]} reads '{Clip(now, 60)}' after the move (BOM item numbers may renumber).");
            changed.Add($"row {mr[0]} → {mr[1]}");
        }
        foreach (var c in q.Cells ?? [])
        {
            Range(c.Row, t.RowCount, "cell row"); Range(c.Column, t.ColumnCount, "cell column");
            string want = c.Text ?? "";
            if (!t.IsCellTextEditable(c.Row, c.Column))
                ctx.Warn($"Cell [{c.Row}, {c.Column}] is computed by SOLIDWORKS (a BOM's quantity or property column); typed text overrides it.");
            t.Text[c.Row, c.Column] = want;
            string got = t.Text[c.Row, c.Column] ?? "";
            if (DrawingEdit.Plain(got) != DrawingEdit.Plain(want))
                throw new SwException(ErrorCodes.VerifyFailed, $"Cell [{c.Row}, {c.Column}] reads '{Clip(got, 60)}', not '{Clip(want, 60)}' (cells before it were written).");
        }
        if (q.Cells is { Count: > 0 }) changed.Add($"{q.Cells.Count} cell(s) written and read back");
        foreach (var (k, w) in q.ColumnWidthsMm ?? new Dictionary<string, double>())
        {
            int col = int.Parse(k);
            Range(col, t.ColumnCount, "columnWidthsMm column");
            t.SetColumnWidth(col, Units.M(w), (int)swTableRowColSizeChangeBehavior_e.swTableRowColChange_TableSizeCanChange);
            if (Math.Abs(t.GetColumnWidth(col) - Units.M(w)) > 1e-5) throw new SwException(ErrorCodes.VerifyFailed, $"Column {col} is {Units.Mm(t.GetColumnWidth(col))} mm wide, not {w} mm.");
            changed.Add($"column {col} width {w} mm");
        }
        if (q.RowHeightMm is double rh)
        {
            for (int r = 0; r < t.RowCount; r++) t.SetRowHeight(r, Units.M(rh), (int)swTableRowColSizeChangeBehavior_e.swTableRowColChange_TableSizeCanChange);
            var off = Enumerable.Range(0, t.RowCount).Where(r => Math.Abs(t.GetRowHeight(r) - Units.M(rh)) > 1e-5).ToList();
            if (off.Count > 0) ctx.Warn($"{off.Count} row(s) are taller than {rh} mm (their text needs more room).");
            changed.Add($"row height {rh} mm");
        }
        var undo = new List<Action>();
        var problems = new List<string>();
        if (q.TextHeightMm is double th) SetHeight(a, Units.M(th), changed, problems, undo);
        if (q.AtMm is not null) Move(ctx, d, a, Sheet2(q.AtMm, "atMm"), changed, problems, undo, checks);
        if (problems.Count > 0) throw new SwException(ErrorCodes.VerifyFailed, $"'{a.Name}': {string.Join("; ", problems)}.");
        if (changed.Count == 0) throw SwException.InvalidArgument("Nothing to change.", "Pass cells, columnWidthsMm, rowHeightMm, textHeightMm, moveRow, moveColumn, insertColumn, columnProperty and/or atMm.");
        d.GraphicsRedraw2();
        var cells = Enumerable.Range(0, Math.Min(t.RowCount, MaxBomRows))
            .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, t.ColumnCount).Select(c => t.DisplayedText[r, c] ?? "").ToList()).ToList();
        return new DrwTableSetResult { Doc = ctx.Title(d), Table = a.Name, Rows = t.RowCount, Columns = t.ColumnCount, Changed = changed, Cells = cells, Checks = checks.Count > 0 ? checks : null };
    }
}
