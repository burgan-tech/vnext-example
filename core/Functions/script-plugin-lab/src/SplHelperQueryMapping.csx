using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using ScriptPluginLab.Helpers;

/// <summary>
/// script-plugin-lab — HELPER path: the mapping reaches the plugin DLL only through the sys-mappings
/// helper spl-db-helper (scripts.helpers), the way a team's database helper is consumed. Same query as
/// spl-direct-query; the two together prove both paths run the SAME loaded copy of the plugin.
/// </summary>
public class SplHelperQueryMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var connectionString = GetConfigValue("ScriptPluginLab:ConnectionString");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ScriptResponse
            {
                Data = new Dictionary<string, object?> { ["path"] = "helper", ["configured"] = false }
            };
        }

        var data = await SplDb.NextAsync(connectionString, 41, context.CancellationToken);
        data["path"] = "helper";
        data["configured"] = true;
        data["tokenCanBeCanceled"] = context.CancellationToken.CanBeCanceled;
        return new ScriptResponse { Data = data };
    }
}
