using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SolidWorksMcp.Core.Services;

/// <summary>A dimension tolerance as the drawing tools take it (mm, or degrees for angular dimensions).</summary>
public sealed record DimTolerance
{
    /// <summary>none | basic | bilateral | limit | symmetric | min | max | fit.</summary>
    public string? Type { get; init; }
    /// <summary>Upper deviation (bilateral, limit; symmetric: the ± value), e.g. 0.1 for +0.1.</summary>
    public double? PlusMm { get; init; }
    /// <summary>Lower deviation as a magnitude, e.g. 0.05 for −0.05 (bilateral, limit).</summary>
    public double? MinusMm { get; init; }
    /// <summary>fit: the hole class, e.g. "H7".</summary>
    public string? Hole { get; init; }
    /// <summary>fit: the shaft class, e.g. "g6".</summary>
    public string? Shaft { get; init; }
}

/// <summary>A tolerance resolved to the API: swTolType_e, and SetValues2's (min, max) deviations in mm.</summary>
internal sealed record TolPlan(string Name, int Code, double MinMm, double MaxMm, string? Hole, string? Shaft)
{
    public bool HasValues => Name is "bilateral" or "limit" or "symmetric" or "min" or "max";
}

/// <summary>Where a stacked dimension goes: its index in the input and the text position (sheet mm).</summary>
internal readonly record struct StackSlot(int Index, double X, double Y);

/// <summary>One problem drw_check found.</summary>
internal sealed record SheetIssue(string Kind, string Item, string Detail, string? Other = null);

/// <summary>What drw_check knows about one annotation (sheet mm).</summary>
internal sealed record CheckItem(string Name, string Type, string View, SheetRect? Box, bool Dangling, int Attached,
    double? Value, string? Prefix, string? DimKind, bool OnSheet)
{
    /// <summary>The dimension's measured value for duplicate detection: kind and value to 1e-4 mm.</summary>
    public string? DuplicateKey => Type == "dimension" && Value is double v ? $"{View}|{DimKind}|{Math.Round(v, 4).ToString(CultureInfo.InvariantCulture)}" : null;
}

/// <summary>
/// The pure parts of the drawing-editing tools (offline-tested): tolerance and precision arguments, note text escapes,
/// annotation extents, the sheet check, dimension stacking and clamping a text position into the sheet. Lengths are
/// sheet mm unless noted.
/// </summary>
internal static class DrawingEdit
{
    public static string? Norm(string? s) => DrawingLayout.Norm(s);

    // ------------------------------------------------------------------ tolerances and precision

    /// <summary>swTolType_e codes (the 2025 interop: none 0, basic 1, bilateral 2, limit 3, symmetric 4, min 5, max 6, fit 7).</summary>
    public static TolPlan Tolerance(DimTolerance t, bool angular, string arg = "tolerance")
    {
        string name = Norm(t.Type) switch
        {
            null when t.PlusMm is not null || t.MinusMm is not null => t.MinusMm is null ? "symmetric" : "bilateral",
            null when t.Hole is not null || t.Shaft is not null => "fit",
            "none" or "off" or "nominal" => "none",
            "basic" => "basic",
            "bilateral" or "bilat" or "plus_minus" => "bilateral",
            "limit" or "limits" => "limit",
            "symmetric" or "sym" or "plusminus" or "±" => "symmetric",
            "min" or "minimum" => "min",
            "max" or "maximum" => "max",
            "fit" or "iso_fit" => "fit",
            null => throw SwException.InvalidArgument($"'{arg}.type' is required: none, basic, bilateral, limit, symmetric, min, max or fit."),
            _ => throw SwException.InvalidArgument($"Unknown {arg}.type '{t.Type}'.", "Use none, basic, bilateral, limit, symmetric, min, max or fit."),
        };
        string u = angular ? "deg" : "mm";
        double Dev(double? v, string what)
        {
            if (v is not double d || !double.IsFinite(d)) throw SwException.InvalidArgument($"{arg}: '{name}' needs {what} (in {u}).");
            if (Math.Abs(d) > 1000) throw SwException.InvalidArgument($"{arg}: {what} {d} {u} is not a tolerance.");
            return d;
        }
        void None(params (string Name, object? Value)[] fields)
        {
            foreach (var (n, v) in fields)
                if (v is not null) throw SwException.InvalidArgument($"{arg}: '{n}' does not apply to a {name} tolerance.");
        }
        switch (name)
        {
            case "none":
            case "basic":
                None(("plusMm", t.PlusMm), ("minusMm", t.MinusMm), ("hole", t.Hole), ("shaft", t.Shaft));
                return new TolPlan(name, name == "none" ? 0 : 1, 0, 0, null, null);
            case "symmetric":
            {
                None(("minusMm", t.MinusMm), ("hole", t.Hole), ("shaft", t.Shaft));
                double p = Math.Abs(Dev(t.PlusMm, "plusMm (the ± value)"));
                if (p == 0) throw SwException.InvalidArgument($"{arg}: a symmetric tolerance of 0 is no tolerance.", "Use type 'none'.");
                return new TolPlan(name, 4, -p, p, null, null);
            }
            case "bilateral":
            case "limit":
            {
                None(("hole", t.Hole), ("shaft", t.Shaft));
                double p = Dev(t.PlusMm, "plusMm (upper deviation)");
                double m = Dev(t.MinusMm, "minusMm (lower deviation, as a magnitude: 0.05 for −0.05)");
                // A negative minusMm means a lower deviation above nominal (e.g. +0.2/+0.1): allowed, but it must stay below the upper one.
                if (p < -m) throw SwException.InvalidArgument($"{arg}: the upper deviation +{p} is below the lower one −{m}.");
                return new TolPlan(name, name == "bilateral" ? 2 : 3, -m, p, null, null);
            }
            case "min":
            case "max":
            {
                None(("hole", t.Hole), ("shaft", t.Shaft));
                double p = Dev(name == "max" ? t.PlusMm : t.MinusMm ?? t.PlusMm, name == "max" ? "plusMm" : "minusMm");
                return new TolPlan(name, name == "min" ? 5 : 6, name == "min" ? -Math.Abs(p) : 0, name == "max" ? Math.Abs(p) : 0, null, null);
            }
            default: // fit
            {
                None(("plusMm", t.PlusMm), ("minusMm", t.MinusMm));
                if (angular) throw SwException.InvalidArgument($"{arg}: fits apply to linear and diameter dimensions only.");
                string? hole = FitClass(t.Hole, upper: true, $"{arg}.hole");
                string? shaft = FitClass(t.Shaft, upper: false, $"{arg}.shaft");
                if (hole is null && shaft is null) throw SwException.InvalidArgument($"{arg}: a fit needs 'hole' (e.g. H7) and/or 'shaft' (e.g. g6).");
                return new TolPlan(name, 7, 0, 0, hole, shaft);
            }
        }
    }

    /// <summary>An ISO 286 tolerance class: hole classes are upper case (H7), shaft classes lower case (g6).</summary>
    private static string? FitClass(string? s, bool upper, string arg)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string c = s.Trim();
        if (!Regex.IsMatch(c, @"^[A-Za-z]{1,2}\d{1,2}$"))
            throw SwException.InvalidArgument($"'{arg}' must be an ISO 286 class like {(upper ? "H7" : "g6")} (got '{s}').");
        string letters = Regex.Match(c, "^[A-Za-z]+").Value;
        return (upper ? letters.ToUpperInvariant() : letters.ToLowerInvariant()) + c[letters.Length..];
    }

    /// <summary>A tolerance read back from SOLIDWORKS: its type name for swTolType_e.</summary>
    public static string TolName(int code) => code switch
    {
        0 => "none", 1 => "basic", 2 => "bilateral", 3 => "limit", 4 => "symmetric", 5 => "min", 6 => "max",
        7 => "fit", 8 => "fit_with_tolerance", 9 => "fit_tolerance_only", 10 => "block", 11 => "general", _ => $"type {code}",
    };

    /// <summary>Decimal places 0..8; null = leave as it is; "document" (as -1 here) = follow the document setting.</summary>
    public static int? Precision(int? places, string arg)
    {
        if (places is null) return null;
        if (places == -1) return -1;
        if (places is < 0 or > 8) throw SwException.InvalidArgument($"'{arg}' must be 0..8 decimal places, or -1 to follow the document (got {places}).");
        return places;
    }

    // ------------------------------------------------------------------ text

    /// <summary>
    /// Note text as SOLIDWORKS takes it: real line breaks, and the two-character escape <c>\n</c> (what agents often send
    /// when they mean a line break) as one too; <c>\\n</c> stays a literal backslash-n. Lines are joined with CR LF.
    /// </summary>
    public static string NoteText(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char n = text[i + 1];
                if (n == '\\' && i + 2 < text.Length && text[i + 2] == 'n') { sb.Append("\\n"); i += 2; continue; }
                if (n == 'n') { sb.Append('\n'); i++; continue; }
                if (n == 'r' && i + 3 < text.Length && text[i + 2] == '\\' && text[i + 3] == 'n') { sb.Append('\n'); i += 3; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    }

    /// <summary>Text as read back, with LF line breaks, for comparisons.</summary>
    public static string Plain(string? s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// True when a note's text is linked to properties ($PRP:"…", $PRPSHEET:"…", $PRPMODEL…) or carries formatting tags;
    /// the evaluated text then differs from the raw one.
    /// </summary>
    public static bool HasLinks(string? raw) => raw is not null && (raw.Contains('$') || raw.Contains('<'));

    /// <summary>The property names a raw note text links to, e.g. $PRPSHEET:"Description" → Description (sheet scope).</summary>
    public static IReadOnlyList<(string Scope, string Name)> Links(string? raw)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrEmpty(raw)) return list;
        foreach (Match m in Regex.Matches(raw, @"\$(PRPSHEET|PRPSMODEL|PRPMODEL|PRPVIEW|PRP)\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase))
        {
            string scope = m.Groups[1].Value.ToUpperInvariant() switch
            {
                "PRPSHEET" or "PRPSMODEL" => "model",  // the model shown on the sheet (first view)
                "PRPMODEL" or "PRPVIEW" => "view_model",
                _ => "drawing",
            };
            list.Add((scope, m.Groups[2].Value));
        }
        return list;
    }

    /// <summary>The link text for a property: scope drawing → $PRP:"name", model → $PRPSHEET:"name".</summary>
    public static string LinkText(string scope, string property)
    {
        if (string.IsNullOrWhiteSpace(property) || property.Contains('"'))
            throw SwException.InvalidArgument($"'{property}' is not a property name (no quotes).");
        return (Norm(scope) ?? "drawing") switch
        {
            "drawing" or "prp" => $"$PRP:\"{property.Trim()}\"",
            "model" or "sheet" or "prpsheet" => $"$PRPSHEET:\"{property.Trim()}\"",
            _ => throw SwException.InvalidArgument($"Unknown link scope '{scope}'.", "Use drawing ($PRP, the drawing's properties) or model ($PRPSHEET, the model on the sheet)."),
        };
    }

    // ------------------------------------------------------------------ extents

    /// <summary>The bounding rectangle of points (m) in mm, grown by <paramref name="padMm"/>; null without points.</summary>
    public static SheetRect? Extent(IEnumerable<(double X, double Y)> pointsM, double padMm = 0)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        bool any = false;
        foreach (var (x, y) in pointsM)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            any = true;
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        if (!any) return null;
        return new SheetRect(x0 * 1000 - padMm, y0 * 1000 - padMm, x1 * 1000 + padMm, y1 * 1000 + padMm);
    }

    /// <summary>
    /// The rough box of a text run (mm): its reference point, height and character count, growing right and up (SOLIDWORKS
    /// reports the lower-left point of each text run). The width is an estimate (0.7 × height per character).
    /// </summary>
    public static SheetRect TextBox(double xMm, double yMm, double heightMm, int chars, double angleRad = 0)
    {
        double w = Math.Max(1, chars) * heightMm * 0.7, h = heightMm;
        if (Math.Abs(angleRad) < 1e-6) return new SheetRect(xMm, yMm, xMm + w, yMm + h);
        double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
        var pts = new[] { (0.0, 0.0), (w, 0.0), (0.0, h), (w, h) }.Select(p => (X: xMm + p.Item1 * c - p.Item2 * s, Y: yMm + p.Item1 * s + p.Item2 * c)).ToList();
        return new SheetRect(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
    }

    public static SheetRect Union(SheetRect a, SheetRect b) => new(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1));

    // ------------------------------------------------------------------ sheet check

    /// <summary>
    /// drw_check's rules: annotations that overlap each other (text boxes; leaders are not counted), sit outside the
    /// drawing frame, are attached to nothing (dimensions, balloons, datums, …: notes may stand free) or to deleted
    /// geometry (dangling), duplicate dimensions (same view, kind and value), and linear dimensions whose value equals a
    /// cylinder diameter of the model but show no Ø.
    /// </summary>
    public static List<SheetIssue> Check(IReadOnlyList<CheckItem> items, SheetRect frame, IReadOnlyCollection<double> diametersMm,
        double overlapGapMm = 0)
    {
        var issues = new List<SheetIssue>();
        var boxed = items.Where(i => i.Box is not null).ToList();
        for (int i = 0; i < boxed.Count; i++)
            for (int j = i + 1; j < boxed.Count; j++)
            {
                var a = boxed[i].Box!.Value;
                var b = boxed[j].Box!.Value;
                if (a.Overlaps(b, -overlapGapMm))
                {
                    double ox = Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0), oy = Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0);
                    if (ox > 0.2 && oy > 0.2) // touching boxes are fine
                        issues.Add(new SheetIssue("overlap", boxed[i].Name, $"overlaps by {Math.Round(ox, 1)} x {Math.Round(oy, 1)} mm", boxed[j].Name));
                }
            }
        foreach (var it in boxed)
            if (!it.Box!.Value.Inside(frame, 0.5))
                issues.Add(new SheetIssue("outside_frame", it.Name, $"box [{string.Join(", ", it.Box.Value.ToArray().Select(F))}] mm is not inside the frame [{string.Join(", ", frame.ToArray().Select(F))}] mm"));
        foreach (var it in items)
        {
            if (it.Dangling) issues.Add(new SheetIssue("dangling", it.Name, "attached to geometry that no longer exists"));
            else if (it.Attached == 0 && it.Type is "dimension" or "balloon" or "datum" or "gtol" or "surface_finish" or "center_mark")
                issues.Add(new SheetIssue("unattached", it.Name, $"a {it.Type.Replace('_', ' ')} attached to nothing"));
        }
        foreach (var g in items.Where(i => i.DuplicateKey is not null).GroupBy(i => i.DuplicateKey!).Where(g => g.Count() > 1))
        {
            var list = g.ToList();
            for (int k = 1; k < list.Count; k++)
                issues.Add(new SheetIssue("duplicate", list[k].Name, $"the same {list[k].DimKind} value {F(list[k].Value ?? 0)} as", list[0].Name));
        }
        foreach (var it in items.Where(i => i.Type == "dimension" && i.DimKind is "linear" or "horizontal" or "vertical" && i.Value is not null))
        {
            bool hasDia = (it.Prefix ?? "").Contains('Ø') || (it.Prefix ?? "").Contains("<MOD-DIAM>", StringComparison.OrdinalIgnoreCase) ||
                          (it.Prefix ?? "").Contains("%%c", StringComparison.OrdinalIgnoreCase);
            if (!hasDia && diametersMm.Any(dd => Math.Abs(dd - it.Value!.Value) < 1e-4))
                issues.Add(new SheetIssue("missing_diameter_symbol", it.Name,
                    $"a linear {F(it.Value!.Value)} mm equals a cylinder diameter of the model but has no Ø prefix (if it measures that diameter, set prefix 'Ø' with drw_dimension_set)"));
        }
        return issues;
    }

    private static string F(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ stacking and clamping

    /// <summary>
    /// Text positions that stack dimensions on one side of a view (above | below | left | right): the first row
    /// <paramref name="offsetMm"/> outside the outline, then one row every <paramref name="spacingMm"/>, the smallest value
    /// nearest the view (so extension lines don't cross). Each dimension keeps its position along the side.
    /// </summary>
    public static List<StackSlot> Stack(IReadOnlyList<(double X, double Y, double Value)> dims, SheetRect outline, string side,
        double offsetMm, double spacingMm)
    {
        if (!double.IsFinite(spacingMm) || spacingMm < 2 || spacingMm > 100)
            throw SwException.InvalidArgument($"spacingMm must be between 2 and 100 (got {spacingMm}).");
        if (!double.IsFinite(offsetMm) || offsetMm < 0 || offsetMm > 200)
            throw SwException.InvalidArgument($"offsetMm must be between 0 and 200 (got {offsetMm}).");
        string s = Norm(side) switch
        {
            "above" or "top" or "up" => "above",
            "below" or "bottom" or "down" => "below",
            "left" => "left",
            "right" => "right",
            _ => throw SwException.InvalidArgument($"Unknown side '{side}'.", "Use above, below, left or right."),
        };
        var order = dims.Select((d, i) => (d, i)).OrderBy(t => t.d.Value).ThenBy(t => t.i).ToList();
        var slots = new List<StackSlot>();
        for (int k = 0; k < order.Count; k++)
        {
            var (d, i) = order[k];
            double off = offsetMm + k * spacingMm;
            slots.Add(s switch
            {
                "above" => new StackSlot(i, d.X, outline.Y1 + off),
                "below" => new StackSlot(i, d.X, outline.Y0 - off),
                "left" => new StackSlot(i, outline.X0 - off, d.Y),
                _ => new StackSlot(i, outline.X1 + off, d.Y),
            });
        }
        return slots.OrderBy(sl => sl.Index).ToList();
    }

    /// <summary>
    /// Moves a text position into <paramref name="area"/> (the drawing frame) so that a box of the given size around it
    /// fits; returns the point and whether it moved.
    /// </summary>
    public static (double X, double Y, bool Moved) Clamp(double x, double y, double wMm, double hMm, SheetRect area)
    {
        double hw = Math.Min(wMm / 2, area.W / 2), hh = Math.Min(hMm / 2, area.H / 2);
        double nx = Math.Clamp(x, area.X0 + hw, area.X1 - hw), ny = Math.Clamp(y, area.Y0 + hh, area.Y1 - hh);
        return (nx, ny, Math.Abs(nx - x) > 1e-9 || Math.Abs(ny - y) > 1e-9);
    }

    /// <summary>
    /// The position correction that brings a view's outline to a target: its centre to <paramref name="cx"/>, <paramref name="cy"/>
    /// (or, with a target outline, its lower-left corner to the target's). IView.Position isn't the outline's centre for every
    /// view type, so the tools move by the difference and read the outline again.
    /// </summary>
    public static (double Dx, double Dy) OutlineCorrection(SheetRect now, double? cx, double? cy, SheetRect? target)
    {
        if (target is SheetRect t) return (t.X0 - now.X0, t.Y0 - now.Y0);
        return ((cx ?? now.Cx) - now.Cx, (cy ?? now.Cy) - now.Cy);
    }

    // ------------------------------------------------------------------ names

    /// <summary>
    /// True when <paramref name="query"/> names this annotation: its annotation name ("DetailItem12"), the dimension's full
    /// name ("RD1@Drawing View1", "D1@Sketch1@block.Part"), or name@view.
    /// </summary>
    public static bool NameMatches(string query, string annotationName, string? dimensionFullName, string view)
    {
        string q = query.Trim();
        if (q.Length == 0) return false;
        bool Eq(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (Eq(annotationName, q) || Eq(dimensionFullName, q)) return true;
        int at = q.LastIndexOf('@');
        if (at > 0)
        {
            string n = q[..at], v = q[(at + 1)..];
            if (Eq(view, v) && (Eq(annotationName, n) || Eq(ShortDim(dimensionFullName), n))) return true;
        }
        return Eq(ShortDim(dimensionFullName), q) && dimensionFullName!.Count(c => c == '@') <= 1;
    }

    /// <summary>The first part of a dimension name ("D1@Sketch1@part.Part" → "D1").</summary>
    public static string? ShortDim(string? fullName) => fullName is null ? null : fullName.Split('@')[0];
}
