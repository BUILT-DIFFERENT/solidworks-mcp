using System.ComponentModel;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Drafting standards and named document options (leading zeros, decimals, arrows, text heights, …).</summary>
[McpServerToolType, Toolset("documents")]
public sealed class StandardTools(SwSession session)
{
    private readonly StandardService _svc = new(session);

    [McpServerTool(Name = "doc_standard", Title = "Drafting standard and options", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Drafting standard / document options: load a .sldstd, set options by name (standard, dimension_decimals, " +
                 "tolerance_decimals, angle_decimals, leading_zeros, trailing_zeros, dimension_arrow, note_alignment, arrow and text " +
                 "sizes; or any swUserPreference enum name, or kind:id), save a .sldstd, and list every option that differs from another " +
                 "open document. Each write is read back; the named options are always returned.")]
    public async Task<string> DocStandard(
        [Description(".sldstd file to load.")] string? load = null,
        [Description("Options to set, e.g. {\"leading_zeros\":\"remove\",\"dimension_decimals\":\"2\",\"dimension_arrow\":\"closed\"}.")] Dictionary<string, string>? set = null,
        [Description(".sldstd file to save the standard to (after load and set).")] string? save = null,
        [Description("Allow replacing an existing save file (backed up first).")] bool overwrite = false,
        [Description("Open document (handle, title or path) to diff every option against.")] string? compareWith = null,
        [Description("Maximum differences to return.")] int limit = 100,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.StandardAsync(new DocStandardRequest
        {
            Doc = doc, Load = load, Set = set, Save = save, Overwrite = overwrite, CompareWith = compareWith, Limit = limit,
        }, cancellationToken));
}
