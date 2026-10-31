using System.Globalization;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core.Services;

/// <summary>doc_standard: load or save a drafting standard, set document options by name, compare with another document.</summary>
public sealed record DocStandardRequest
{
    public string? Doc { get; init; }
    /// <summary>A .sldstd file to load into the document.</summary>
    public string? Load { get; init; }
    /// <summary>Document options to set: name → value (see <see cref="DraftingOptions"/>).</summary>
    public IReadOnlyDictionary<string, string>? Set { get; init; }
    /// <summary>A .sldstd file to save the document's standard to (after load and set).</summary>
    public string? Save { get; init; }
    public bool Overwrite { get; init; }
    /// <summary>Another open document (handle, title or path) whose options are compared with this one's.</summary>
    public string? CompareWith { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed class DocOptionValue
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public string? Previous { get; init; }
    public string? What { get; init; }
}

public sealed class DocOptionDiff
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string Other { get; init; }
}

public sealed class DocStandardResult : ToolResult
{
    public string? Loaded { get; init; }
    public string? Saved { get; init; }
    public string? BackupPath { get; init; }
    /// <summary>The named options as they read now (always), with the previous value where this call changed them.</summary>
    public required IReadOnlyList<DocOptionValue> Options { get; init; }
    /// <summary>How many options loading the standard changed.</summary>
    public int? ChangedByLoad { get; init; }
    public string? ComparedWith { get; init; }
    public IReadOnlyList<DocOptionDiff>? Differences { get; init; }
    public int? DifferenceCount { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>
/// Drafting standards and document options (DESIGN §4.12 area): IModelDocExtension.LoadDraftingStandard /
/// SaveDraftingStandard, and Get/SetUserPreference* at document level (document options need no journal: they belong to
/// the document, like units). Every write is read back.
/// </summary>
public sealed class StandardService(SwSession session)
{
    private static readonly (PrefKind Kind, Type Enum)[] Enums =
    [
        (PrefKind.Toggle, typeof(swUserPreferenceToggle_e)),
        (PrefKind.Integer, typeof(swUserPreferenceIntegerValue_e)),
        (PrefKind.Double, typeof(swUserPreferenceDoubleValue_e)),
        (PrefKind.String, typeof(swUserPreferenceStringValue_e)),
    ];

    public Task<DocStandardResult> StandardAsync(DocStandardRequest q, CancellationToken ct = default)
    {
        if (q.Limit is < 1 or > 2000) throw SwException.InvalidArgument($"limit must be 1..2000 (got {q.Limit}).");
        string? load = null;
        if (q.Load is not null)
        {
            if (!q.Load.Trim().EndsWith(".sldstd", StringComparison.OrdinalIgnoreCase)) throw SwException.InvalidArgument("'load' must be a .sldstd drafting standard file.");
            load = session.Paths.ForRead(q.Load, "load");
        }
        if (q.Save is not null && !q.Save.Trim().EndsWith(".sldstd", StringComparison.OrdinalIgnoreCase))
            throw SwException.InvalidArgument("'save' must name a .sldstd file.");
        var set = (q.Set ?? new Dictionary<string, string>()).Select(kv => (Opt: DraftingOptions.Find(kv.Key, ByEnumName), kv.Value)).ToList();
        var plans = set.Select(s => (s.Opt, Api: s.Opt.ToApi(s.Value))).ToList(); // validated before any COM call
        bool writes = load is not null || plans.Count > 0;
        var options = (writes ? RunOptions.Write(q.Doc) : RunOptions.Read(q.Doc)) with { Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 90)) };
        return session.RunAsync("doc_standard", ctx =>
        {
            var d = ctx.Doc;
            var ext = d.Extension;
            var checks = new List<string>();
            int? changedByLoad = null;
            if (load is not null)
            {
                var before = ReadAll(ext);
                if (!ext.LoadDraftingStandard(load))
                    throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS did not load the drafting standard '{load}'.", "Check that it is a .sldstd file of this SOLIDWORKS version.");
                var after = ReadAll(ext);
                changedByLoad = DraftingOptions.Diff(before, after).Count;
                checks.Add($"loaded '{Path.GetFileName(load)}': {changedByLoad} option(s) changed");
            }
            var previous = new Dictionary<string, string>();
            foreach (var (opt, api) in plans)
            {
                string old = Read(ext, opt);
                previous[opt.Name] = opt.FromApi(old);
                Write(ext, opt, api);
                string now = Read(ext, opt);
                if (!SameApi(opt.Kind, now, api))
                    throw new SwException(ErrorCodes.VerifyFailed, $"'{opt.Name}' reads {opt.FromApi(now)}, not {opt.FromApi(api)} (options set before it in this call stay set).",
                        "Some options only apply to some document types or standards; check the value.");
            }
            if (plans.Count > 0) checks.Add($"{plans.Count} option(s) set and read back");
            string? saved = null, backup = null;
            if (q.Save is not null)
            {
                saved = ctx.Paths.ForWrite(q.Save, q.Overwrite, out backup, "save");
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                DateTime t0 = DateTime.UtcNow.AddSeconds(-2);
                if (!ext.SaveDraftingStandard(saved) || !File.Exists(saved) || File.GetLastWriteTimeUtc(saved) < t0 || new FileInfo(saved).Length == 0)
                    throw new SwException(ErrorCodes.IoError, $"The drafting standard was not written to '{saved}'.");
                checks.Add($"saved {new FileInfo(saved).Length} bytes");
            }
            var shown = DraftingOptions.All.Concat(plans.Select(p => p.Opt)).DistinctBy(o => o.Name)
                .Select(o =>
                {
                    string v;
                    try { v = o.FromApi(Read(ext, o)); }
                    catch (COMException e) when (!ComArgs.IsDisconnect(e)) { v = "?"; }
                    return new DocOptionValue { Name = o.Name, Value = v, Previous = previous.TryGetValue(o.Name, out var p) && p != v ? p : null, What = o.What };
                }).ToList();
            List<DocOptionDiff>? diffs = null;
            int? diffCount = null;
            string? other = null;
            if (q.CompareWith is not null)
            {
                var od = DocLocator.Find(ctx.Documents, ctx.App.Documents(), q.CompareWith);
                if (DocLocator.SameDoc(od, d)) throw SwException.InvalidArgument("'compareWith' is the same document.");
                other = ctx.Title(od);
                var all = DraftingOptions.Diff(ReadAll(ext), ReadAll(od.Extension));
                diffCount = all.Count;
                diffs = all.Take(q.Limit).Select(x => new DocOptionDiff { Name = x.Name, Value = x.Mine, Other = x.Other }).ToList();
                checks.Add($"{all.Count} document option(s) differ from '{other}'");
            }
            return new DocStandardResult
            {
                Doc = ctx.Title(d), Loaded = load, Saved = saved, BackupPath = backup, Options = shown, ChangedByLoad = changedByLoad,
                ComparedWith = other, Differences = diffs, DifferenceCount = diffCount, Checks = checks,
            };
        }, options, ct);
    }

    /// <summary>An enum member name (swDetailingLinearDimPrecision) → its kind and id.</summary>
    private static (PrefKind, int)? ByEnumName(string name)
    {
        foreach (var (kind, e) in Enums)
            if (Enum.TryParse(e, name.Trim(), ignoreCase: true, out object? v) && v is not null) return (kind, Convert.ToInt32(v, CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>Every document option the interop names, by "enum member" → value (options that can't be read are left out).</summary>
    private static Dictionary<string, string> ReadAll(IModelDocExtension ext)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, e) in Enums)
            foreach (var name in Enum.GetNames(e).Distinct())
            {
                int id = Convert.ToInt32(Enum.Parse(e, name), CultureInfo.InvariantCulture);
                try { all[name] = Read(ext, new DraftOption(name, kind, id, 0, "")); }
                catch (Exception ex) when (ex is COMException or ArgumentException && !ComArgs.IsDisconnect(ex)) { /* not a document option */ }
            }
        return all;
    }

    private static string Read(IModelDocExtension ext, DraftOption o)
    {
        if (o.TextHeight)
            return ((object?)ext.GetUserPreferenceTextFormat(o.Id, o.Option) as ITextFormat)?.CharHeight.ToString("R", CultureInfo.InvariantCulture) ?? "?";
        return o.Kind switch
        {
            PrefKind.Toggle => ext.GetUserPreferenceToggle(o.Id, o.Option) ? "True" : "False",
            PrefKind.Integer => ext.GetUserPreferenceInteger(o.Id, o.Option).ToString(CultureInfo.InvariantCulture),
            PrefKind.Double => ext.GetUserPreferenceDouble(o.Id, o.Option).ToString("R", CultureInfo.InvariantCulture),
            _ => ext.GetUserPreferenceString(o.Id, o.Option) ?? "",
        };
    }

    private static void Write(IModelDocExtension ext, DraftOption o, string api)
    {
        bool ok;
        if (o.TextHeight)
        {
            var tf = (object?)ext.GetUserPreferenceTextFormat(o.Id, o.Option) as ITextFormat
                     ?? throw new SwException(ErrorCodes.NotSupported, $"'{o.Name}' has no text format in this document.");
            tf.CharHeight = double.Parse(api, CultureInfo.InvariantCulture);
            ok = ext.SetUserPreferenceTextFormat(o.Id, o.Option, (TextFormat)(object)tf);
        }
        else
        {
            ok = o.Kind switch
            {
                PrefKind.Toggle => ext.SetUserPreferenceToggle(o.Id, o.Option, api == "True"),
                PrefKind.Integer => ext.SetUserPreferenceInteger(o.Id, o.Option, int.Parse(api, CultureInfo.InvariantCulture)),
                PrefKind.Double => ext.SetUserPreferenceDouble(o.Id, o.Option, double.Parse(api, CultureInfo.InvariantCulture)),
                _ => ext.SetUserPreferenceString(o.Id, o.Option, api),
            };
        }
        if (!ok) throw new SwException(ErrorCodes.VerifyFailed, $"SOLIDWORKS refused '{o.Name}' = {o.FromApi(api)}.", "The option may not apply to this document type.");
    }

    private static bool SameApi(PrefKind kind, string a, string b) =>
        kind == PrefKind.Double
            ? double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
              Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x))
            : a == b;
}
