using System.Globalization;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core.Services;

/// <summary>A named document option of doc_standard: where it lives and how its value reads (pure; offline-tested).</summary>
internal sealed record DraftOption(string Name, PrefKind Kind, int Id, int Option, string What, IReadOnlyDictionary<string, int>? Values = null,
    double Scale = 1, double Min = double.MinValue, double Max = double.MaxValue, bool TextHeight = false)
{
    /// <summary>The API value for a tool value: a name from <see cref="Values"/>, a number (mm → m with Scale), or a bool.</summary>
    public string ToApi(string value)
    {
        string v = value.Trim();
        switch (Kind)
        {
            case PrefKind.Toggle:
                return DraftingOptions.Norm(v) switch
                {
                    "true" or "on" or "yes" or "1" => "True",
                    "false" or "off" or "no" or "0" => "False",
                    _ => throw SwException.InvalidArgument($"'{Name}' is on or off (got '{value}')."),
                };
            case PrefKind.Integer:
                if (Values is not null)
                {
                    if (Values.TryGetValue(DraftingOptions.Norm(v) ?? "", out int code)) return code.ToString(CultureInfo.InvariantCulture);
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw) && Values.Values.Contains(raw))
                        return raw.ToString(CultureInfo.InvariantCulture);
                    throw SwException.InvalidArgument($"'{Name}' takes {string.Join(" | ", Values.Keys)} (got '{value}').");
                }
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < Min || n > Max)
                    throw SwException.InvalidArgument($"'{Name}' takes a whole number{(Min > double.MinValue ? $" {Min}..{Max}" : "")} (got '{value}').");
                return n.ToString(CultureInfo.InvariantCulture);
            case PrefKind.Double:
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d) || d < Min || d > Max)
                    throw SwException.InvalidArgument($"'{Name}' takes a number{(Min > double.MinValue ? $" {Min}..{Max}" : "")} (got '{value}').");
                return (d / Scale).ToString("R", CultureInfo.InvariantCulture);
            default:
                return v;
        }
    }

    /// <summary>The tool value for an API value (names where known, mm for lengths).</summary>
    public string FromApi(string api)
    {
        if (Kind == PrefKind.Integer && Values is not null && int.TryParse(api, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c))
            return Values.FirstOrDefault(kv => kv.Value == c).Key ?? api;
        if (Kind == PrefKind.Double && double.TryParse(api, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return Math.Round(d * Scale, 6).ToString(CultureInfo.InvariantCulture);
        return api;
    }
}

/// <summary>
/// The document options doc_standard sets by name: the usual drafting-standard settings (swUserPreference*_e ids of the
/// 2025 interop, with their option values). Others can be given by their enum name (swDetailingLinearDimPrecision) or
/// as kind:id ("integer:24").
/// </summary>
internal static class DraftingOptions
{
    public static string? Norm(string? s) => DrawingLayout.Norm(s);

    private static readonly Dictionary<string, int> Zeros = new() { ["standard"] = 1, ["show"] = 2, ["remove"] = 3 };
    private static readonly Dictionary<string, int> Trailing = new()
    {
        ["smart"] = 0, ["show"] = 1, ["remove"] = 2, ["standard"] = 3, ["remove_only_on_zero"] = 4,
    };
    private static readonly Dictionary<string, int> Arrows = new()
    {
        ["open"] = 0, ["closed"] = 1, ["slash"] = 2, ["dot"] = 3, ["origin"] = 4, ["wide"] = 5, ["iso_wide"] = 6, ["none"] = 10,
    };
    private static readonly Dictionary<string, int> Justify = new() { ["left"] = 1, ["center"] = 2, ["right"] = 3 };
    private static readonly Dictionary<string, int> Standards = new()
    {
        ["ansi"] = 1, ["iso"] = 2, ["din"] = 3, ["jis"] = 4, ["bs"] = 5, ["gost"] = 6, ["gb"] = 7,
    };
    private static readonly Dictionary<string, int> TolTypes = new()
    {
        ["none"] = 0, ["basic"] = 1, ["bilateral"] = 2, ["limit"] = 3, ["symmetric"] = 4, ["min"] = 5, ["max"] = 6,
    };

    public static readonly IReadOnlyList<DraftOption> All =
    [
        new("standard", PrefKind.Integer, 13, 0, "overall drafting standard", Standards),
        new("dimension_decimals", PrefKind.Integer, 24, 0, "decimal places of linear dimensions", Min: 0, Max: 8),
        new("tolerance_decimals", PrefKind.Integer, 25, 0, "decimal places of linear tolerances", Min: 0, Max: 8),
        new("angle_decimals", PrefKind.Integer, 28, 0, "decimal places of angular dimensions", Min: 0, Max: 8),
        new("leading_zeros", PrefKind.Integer, 265, 0, "leading zeros of decimals (0.5 vs .5)", Zeros),
        new("trailing_zeros", PrefKind.Integer, 15, 0, "trailing zeros of dimensions", Trailing),
        new("tolerance_trailing_zeros", PrefKind.Integer, 582, 0, "trailing zeros of tolerances", Trailing),
        new("dimension_arrow", PrefKind.Integer, 16, 0, "arrow style of dimensions", Arrows),
        new("leader_arrow", PrefKind.Integer, 38, 0, "arrow style of leaders on edges and vertices", Arrows),
        new("face_arrow", PrefKind.Integer, 39, 0, "arrow style of leaders on faces", Arrows),
        new("note_alignment", PrefKind.Integer, 30, 0, "text alignment of notes", Justify),
        new("tolerance_style", PrefKind.Integer, 21, 0, "default tolerance of new dimensions", TolTypes),
        new("arrow_length_mm", PrefKind.Double, 26, 0, "arrow length", Scale: 1000, Min: 0.1, Max: 50),
        new("arrow_width_mm", PrefKind.Double, 25, 0, "arrow width", Scale: 1000, Min: 0.1, Max: 50),
        new("arrow_height_mm", PrefKind.Double, 24, 0, "arrow height", Scale: 1000, Min: 0.1, Max: 50),
        new("tolerance_text_scale", PrefKind.Double, 21, 0, "tolerance text size relative to the dimension", Min: 0.1, Max: 2),
        new("dimension_text_height_mm", PrefKind.Double, 1, 0, "text height of dimensions", Scale: 1000, Min: 0.5, Max: 100, TextHeight: true),
        new("note_text_height_mm", PrefKind.Double, 0, 0, "text height of notes", Scale: 1000, Min: 0.5, Max: 100, TextHeight: true),
        new("table_text_height_mm", PrefKind.Double, 11, 0, "text height of BOM tables", Scale: 1000, Min: 0.5, Max: 100, TextHeight: true),
    ];

    public static string Names => string.Join(", ", All.Select(o => o.Name));

    /// <summary>
    /// An option by its name here, by "kind:id[:option]" (toggle | integer | double | string), or by an enum name the
    /// caller resolves (<paramref name="byEnumName"/>: swUserPreference*_e member → kind and id).
    /// </summary>
    public static DraftOption Find(string key, Func<string, (PrefKind Kind, int Id)?> byEnumName)
    {
        string k = key.Trim();
        var named = All.FirstOrDefault(o => o.Name == Norm(k));
        if (named is not null) return named;
        var parts = k.Split(':');
        if (parts.Length is 2 or 3 && Kind(parts[0]) is PrefKind pk && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            int opt = parts.Length == 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) ? o : 0;
            return new DraftOption(k, pk, id, opt, "raw document option");
        }
        if (byEnumName(k) is (PrefKind ek, int eid)) return new DraftOption(k, ek, eid, 0, "document option");
        throw SwException.InvalidArgument($"Unknown option '{key}'.",
            $"Use {Names}; an enum name like swDetailingLinearDimPrecision; or kind:id like integer:24.");
    }

    public static PrefKind? Kind(string s) => Norm(s) switch
    {
        "toggle" or "bool" => PrefKind.Toggle,
        "integer" or "int" => PrefKind.Integer,
        "double" => PrefKind.Double,
        "string" => PrefKind.String,
        _ => null,
    };

    /// <summary>Options whose values differ between two documents: (name, this, other), in a stable order.</summary>
    public static List<(string Name, string Mine, string Other)> Diff(IReadOnlyDictionary<string, string> mine, IReadOnlyDictionary<string, string> other)
    {
        var list = new List<(string, string, string)>();
        foreach (var (name, v) in mine.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (other.TryGetValue(name, out var o) && !SameValue(v, o)) list.Add((name, v, o));
        return list;
    }

    private static bool SameValue(string a, string b)
    {
        if (a == b) return true;
        return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
               double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
               Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x));
    }
}
