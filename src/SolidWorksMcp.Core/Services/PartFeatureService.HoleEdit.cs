using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>feature_hole_edit: a new definition for an existing Hole Wizard feature, by name (feature_hole's names, never type numbers).</summary>
public sealed record HoleEditRequest
{
    public string? Doc { get; init; }
    public string? Feature { get; init; }
    /// <summary>drill | clearance | counterbore | countersink | tapped; default: the hole's current kind.</summary>
    public string? Type { get; init; }
    public string? Standard { get; init; }
    public string? Size { get; init; }
    public double? DiameterMm { get; init; }
    public string? Fit { get; init; }
    /// <summary>blind | through_all.</summary>
    public string? End { get; init; }
    public double? DepthMm { get; init; }
    /// <summary>countersink: top diameter and angle; counterbore: diameter and depth (override the table).</summary>
    public double? CsinkDiameterMm { get; init; }
    public double? CsinkAngleDeg { get; init; }
    public double? CboreDiameterMm { get; init; }
    public double? CboreDepthMm { get; init; }
}

public sealed class HoleEditResult : ToolResult
{
    public required string Feature { get; init; }
    /// <summary>The definition before and after (feature_info's reading).</summary>
    public required FeatureDefinition Before { get; init; }
    public required FeatureDefinition After { get; init; }
    public double VolumeDeltaMm3 { get; init; }
    public required IReadOnlyList<string> Verified { get; init; }
    public required ModelState Model { get; init; }
}

public sealed partial class PartFeatureService
{
    /// <summary>
    /// Edits a Hole Wizard feature in place (IWizardHoleFeatureData2 through ModifyDefinition), so mates and references
    /// to it keep their feature. Kinds, standards, fits and ends are feature_hole's names, planned by the same
    /// <see cref="HolePlan"/>. Verified: the definition read back (kind, size, end, depth), no rebuild error, the main
    /// cylinder at the planned diameter and the volume changed as expected; otherwise undone.
    /// </summary>
    public Task<HoleEditResult> HoleEditAsync(HoleEditRequest q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q.Feature)) throw SwException.InvalidArgument("'feature' is required: the Hole Wizard feature's name, e.g. \"M5 Clearance Hole1\" (feature_list).");
        foreach (var (n, v) in new[] { ("csinkDiameterMm", q.CsinkDiameterMm), ("csinkAngleDeg", q.CsinkAngleDeg), ("cboreDiameterMm", q.CboreDiameterMm), ("cboreDepthMm", q.CboreDepthMm) })
            if (v is double x && (!double.IsFinite(x) || x <= 0 || x > 500)) throw SwException.InvalidArgument($"{n} must be a positive number (got {x}).");
        return session.RunAsync("feature_hole_edit", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            NoOpenSketch(d);
            var f = EntityResolver.FeatureByName(d, q.Feature.Trim())
                    ?? throw new SwException(ErrorCodes.EntityNotFound, $"No feature named '{q.Feature}'.", "feature_list lists the features.");
            if ((object?)f.GetDefinition() is not IWizardHoleFeatureData2 def)
                throw SwException.InvalidArgument($"'{f.Name}' is a {f.GetTypeName2()} feature, not a Hole Wizard hole.");
            var before = FeatureManagementService.Definition(def);
            // Unset arguments keep the hole as it is: its current kind, end and depth feed the plan.
            string kind = q.Type ?? KindOf(before.Type, before.Size);
            string end = q.End ?? (before.EndCondition is "through_all" or "through_all_both" ? "through_all" : "blind");
            double? depth = q.DepthMm ?? (end == "blind" ? before.DepthMm : null);
            double? dia = q.DiameterMm ?? (kind == "drill" && q.Size is null ? before.DiameterMm : null);
            var plan = HolePlan.Parse(kind, q.Standard, q.Size ?? (kind == "drill" ? null : before.Size), dia, q.Fit, end, depth, Experimental);
            var call = plan.Primary;
            if (q.CsinkDiameterMm is not null || q.CsinkAngleDeg is not null)
            {
                if (call.Kind != "countersink") throw SwException.InvalidArgument("csinkDiameterMm/csinkAngleDeg apply to countersink holes.");
                call = call with { CsinkDiameterMm = q.CsinkDiameterMm ?? call.CsinkDiameterMm, CsinkAngleDeg = q.CsinkAngleDeg ?? call.CsinkAngleDeg };
            }
            if (q.CboreDiameterMm is not null || q.CboreDepthMm is not null)
            {
                if (call.Kind != "counterbore") throw SwException.InvalidArgument("cboreDiameterMm/cboreDepthMm apply to counterbore holes.");
                call = call with { CboreDiameterMm = q.CboreDiameterMm ?? call.CboreDiameterMm, CboreDepthMm = q.CboreDepthMm ?? call.CboreDepthMm };
            }

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var snap = Snapshot.Take(d);
            int stamp = d.GetUpdateStamp();
            var checks = new List<string>();
            bool access = def.AccessSelections(d, ComArgs.Nothing);
            bool modified;
            try
            {
                def.InitializeHole(call.GenericType, call.Standard, call.Fastener, call.Size, call.EndType);
                def.EndCondition = call.EndType;
                if (call.DepthMm is double dm) def.Depth = Units.M(dm);
                if (call.Kind == "drill") def.Diameter = Units.M(call.DiameterMm);
                else if (q.DiameterMm is double over) def.ThruHoleDiameter = Units.M(over);
                if (call.CsinkDiameterMm is double csd) def.CounterSinkDiameter = Units.M(csd);
                if (call.CsinkAngleDeg is double csa) def.CounterSinkAngle = Units.Rad(csa);
                if (call.CboreDiameterMm is double cbd) def.CounterBoreDiameter = Units.M(cbd);
                if (call.CboreDepthMm is double cbt) def.CounterBoreDepth = Units.M(cbt);
                modified = f.ModifyDefinition(def, d, ComArgs.Nothing);
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e))
            {
                if (access) def.ReleaseSelectionAccess();
                throw new SwException(ErrorCodes.FeatureFailed, $"'{f.Name}' could not be redefined: {e.Message}.");
            }
            if (!modified)
            {
                if (access) def.ReleaseSelectionAccess();
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS refused the new definition of '{f.Name}' ({call.Kind} {call.Size}).",
                    "Check the size for the standard (feature_hole's sizes), and that blind holes have depthMm.");
            }
            d.EditRebuild3();

            var problems = new List<string>();
            var after = (object?)f.GetDefinition() is IWizardHoleFeatureData2 now ? FeatureManagementService.Definition(now) : null;
            if (after is null) problems.Add("the definition could not be read back");
            else
            {
                string wantEnd = call.DepthMm is null ? "through_all" : "blind";
                if (!(after.EndCondition ?? "").StartsWith(wantEnd, StringComparison.Ordinal)) problems.Add($"its end reads {after.EndCondition}, not {wantEnd}");
                if (call.DepthMm is double want && after.DepthMm is double got && Math.Abs(got - want) > 1e-4) problems.Add($"its depth reads {got} mm, not {want} mm");
                if (problems.Count == 0) checks.Add($"definition read back: {after.Type} {after.Size} {after.EndCondition}{(after.DepthMm is double dd ? $" {dd} mm" : "")}");
            }
            int err = f.GetErrorCode2(out bool warn);
            if (err != 0 && !warn) problems.Add($"rebuild error {err} ({FeatureVerifier.ErrorName(err)})");
            // The main cylinder at the planned diameter.
            double rMin = Units.M(q.DiameterMm ?? call.MinDiameterMm) / 2 - RadiusTol, rMax = Units.M(q.DiameterMm ?? call.MaxDiameterMm) / 2 + RadiusTol;
            var radii = FeatureFaces(f).Select(Topology.Describe).Where(g => g.Surface == "cylinder" && g.Radius is not null).Select(g => g.Radius!.Value).ToList();
            if (radii.Count == 0) problems.Add("the hole has no cylindrical face");
            else if (!radii.Any(r => r >= rMin && r <= rMax))
                problems.Add($"no Ø{(call.MinDiameterMm == call.MaxDiameterMm ? $"{call.DiameterMm}" : $"{call.MinDiameterMm}..{call.MaxDiameterMm}")} mm cylinder (found Ø{string.Join(", Ø", radii.Select(r => Units.Mm(2 * r)).Distinct().Take(6))} mm)");
            else checks.Add($"main cylinder Ø{Units.Mm(2 * radii.First(r => r >= rMin && r <= rMax))} mm");
            var snap2 = Snapshot.Take(d);
            double dv = snap2.Volume - snap.Volume;
            if (snap2.Bodies != snap.Bodies) problems.Add($"the part went from {snap.Bodies} to {snap2.Bodies} bodies");
            if (Math.Abs(dv) <= 1e-12 && after is not null && !Same(before, after)) ctx.Warn("The volume did not change, although the definition did.");
            if (problems.Count > 0)
            {
                string back = "";
                if (d.GetUpdateStamp() != stamp)
                {
                    d.EditUndo2(1);
                    var back0 = Snapshot.Take(d);
                    back = Math.Abs(back0.Volume - snap.Volume) <= Math.Max(1e-12, 1e-9 * snap.Volume) ? " The edit was undone." : " The edit could not be fully undone: check the hole (feature_info).";
                }
                throw new SwException(ErrorCodes.VerifyFailed, $"'{f.Name}': {string.Join("; ", problems)}.{back}",
                    "Check the size and end for this kind; feature_info shows the hole's definition.");
            }
            checks.Add($"volume {(dv < 0 ? "removed" : "added")} {Math.Abs(Units.Mm3(dv))} mm³");
            if (plan.Note is not null) ctx.Warn(plan.Note);
            ctx.Warn("Mates on this hole's faces may need a check: asm_mate_list {feature:'" + f.Name + "'} in the assembly lists them.");
            return new HoleEditResult
            {
                Doc = ctx.Title(d), Feature = f.Name, Before = before, After = after!, VolumeDeltaMm3 = Units.Mm3(dv), Verified = checks, Model = snap2.ToState(),
            };
        }, RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 60)) }, ct);
    }

    /// <summary>feature_hole's kind for a swWzdHoleTypes_e name as feature_info reports it ("counter_bored_drilled", "hole_thru", …).</summary>
    internal static string KindOf(string? type, string? size)
    {
        string t = type ?? "";
        if (t.Contains("tap", StringComparison.Ordinal) || t.Contains("thread", StringComparison.Ordinal)) return "tapped";
        if (t.Contains("counter_bore", StringComparison.Ordinal)) return "counterbore";
        if (t.Contains("counter_sink", StringComparison.Ordinal) || t.Contains("counter_sunk", StringComparison.Ordinal)) return "countersink";
        return size is { } s && s.TrimStart().StartsWith('M') ? "clearance" : "drill";
    }

    private static bool Same(FeatureDefinition a, FeatureDefinition b) =>
        a.Type == b.Type && a.Size == b.Size && a.EndCondition == b.EndCondition && a.DepthMm == b.DepthMm && a.DiameterMm == b.DiameterMm;
}
