using System.Globalization;

namespace SolidWorksMcp.Core.Prefs;

/// <summary>One preference of a restore: the user's value (<see cref="Old"/>) and the last value the server left.</summary>
public sealed record PrefValue(PrefKind Kind, int Pref, string Old, string New);

/// <summary>
/// Puts a unit of preferences back: a single preference, or a group that SOLIDWORKS keeps consistent
/// (<see cref="SystemPrefIds.Coupled"/>). Pure logic: the reads and writes are passed in, so the unit tests can
/// simulate SOLIDWORKS' coupling.
/// </summary>
public static class PrefGroupRestore
{
    public enum Outcome { Restored, AlreadyOriginal, ChangedExternally, Failed }

    /// <summary>
    /// Compare-and-swap for the whole unit: when a value the server changed now reads neither the server's value nor
    /// the original, someone else changed the unit, and it is left alone. Otherwise the originals are written in the
    /// unit's order (for a group: the values others are derived from first), at most twice, and every member is
    /// verified.
    /// </summary>
    public static Outcome Restore(IReadOnlyList<PrefValue> members, Func<PrefKind, int, string> read, Action<PrefKind, int, string> write)
    {
        var current = members.Select(m => read(m.Kind, m.Pref)).ToList();
        if (members.Select((m, i) => Same(m.Kind, current[i], m.Old)).All(x => x)) return Outcome.AlreadyOriginal;
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            bool ours = !Same(m.Kind, m.New, m.Old);
            if (ours && !Same(m.Kind, current[i], m.New) && !Same(m.Kind, current[i], m.Old)) return Outcome.ChangedExternally;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var m in members)
                if (!Same(m.Kind, read(m.Kind, m.Pref), m.Old)) write(m.Kind, m.Pref, m.Old);
            if (members.All(m => Same(m.Kind, read(m.Kind, m.Pref), m.Old))) return Outcome.Restored;
        }
        return Outcome.Failed;
    }

    /// <summary>Value equality as SOLIDWORKS stores it (doubles relative to 1e-12).</summary>
    public static bool Same(PrefKind kind, string a, string b)
    {
        if (kind != PrefKind.Double) return string.Equals(a, b, StringComparison.Ordinal);
        double x = double.Parse(a, CultureInfo.InvariantCulture), y = double.Parse(b, CultureInfo.InvariantCulture);
        return Math.Abs(x - y) <= 1e-12 * Math.Max(1, Math.Abs(x));
    }
}

/// <summary>What to do about an allow-listed system option that changed during a call although no scope wrote it.</summary>
public enum DriftAction
{
    /// <summary>Write the user's value back (and check it).</summary>
    Restore,
    /// <summary>SOLIDWORKS derives it from another option the user set (STL deviation and angle under a coarse or fine STL
    /// quality, recomputed for each document): writing it would switch that option to custom, so it is left as it is.</summary>
    Derived,
}

/// <summary>
/// Options SOLIDWORKS changed by itself during a call (opening a document recomputes the STL deviation, system double 2,
/// every time). Pure: decides per option whether to put the user's value back.
/// </summary>
public static class PrefDrift
{
    /// <summary>STL quality presets (1 coarse, 2 fine): deviation and angle follow them per document.</summary>
    public static bool IsPresetQuality(string? quality) => quality is "1" or "2";

    /// <summary>
    /// The action for each drifted option (kind, id, value before the call). <paramref name="valueNow"/> reads options as
    /// they are now: under a preset STL quality that is still the user's, the STL deviation and angle are derived.
    /// </summary>
    public static List<(PrefKind Kind, int Pref, string Old, DriftAction Action)> Plan(
        IReadOnlyList<(PrefKind Kind, int Pref, string Old)> drifted, Func<PrefKind, int, string?> valueNow,
        IReadOnlyDictionary<(PrefKind Kind, int Pref), string> before)
    {
        before.TryGetValue((PrefKind.Integer, SystemPrefIds.StlQuality), out var qualityBefore);
        string? qualityNow = valueNow(PrefKind.Integer, SystemPrefIds.StlQuality);
        bool presetKept = IsPresetQuality(qualityBefore) && qualityNow == qualityBefore;
        var plan = new List<(PrefKind, int, string, DriftAction)>();
        foreach (var (k, p, old) in drifted)
        {
            bool stlDerived = k == PrefKind.Double && p is SystemPrefIds.StlDeviation or SystemPrefIds.StlAngleTolerance;
            plan.Add((k, p, old, stlDerived && presetKept ? DriftAction.Derived : DriftAction.Restore));
        }
        // Restore the group in its order: the quality before its deviation and angle would be wrong (a custom quality
        // only sticks after them), so order by the coupled group's restore order, then the rest.
        var group = SystemPrefIds.Coupled.SelectMany(g => g).ToList();
        return plan.OrderBy(x => group.IndexOf((x.Item1, x.Item2)) is int i && i >= 0 ? i : int.MaxValue).ToList();
    }
}
