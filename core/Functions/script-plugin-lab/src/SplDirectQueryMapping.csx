using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using ScriptPluginLab.Sql;

/// <summary>
/// script-plugin-lab — HELPER-LESS path: the mapping calls the plugin DLL (ScriptPluginLab.Sql, mounted
/// in Scripting:Sandbox:PluginDirectory and granted through scripts.allowedAssemblies) directly, with no
/// sys-mappings helper in between. Runs one read-only, parameterised query against PostgreSQL.
/// <para>
/// The connection string comes from host configuration (ScriptPluginLab:ConnectionString); when it is
/// not set the mapping answers configured = false and the test skips instead of failing.
/// </para>
/// </summary>
public class SplDirectQueryMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = new Dictionary<string, object?>
        {
            ["path"] = "helperless",
            ["tokenCanBeCanceled"] = context.CancellationToken.CanBeCanceled
        };

        var connectionString = GetConfigValue("ScriptPluginLab:ConnectionString");
        data["configured"] = !string.IsNullOrWhiteSpace(connectionString);
        if (string.IsNullOrWhiteSpace(connectionString))
            return new ScriptResponse { Data = data };

        var probe = await SqlProbe.QueryAsync(
            connectionString,
            "select @n::int + 1 as next, current_database() as db",
            new Dictionary<string, object?> { ["n"] = 41 },
            cancellationToken: context.CancellationToken);

        data["ok"] = probe.Ok;
        data["stage"] = probe.Stage;
        data["error"] = probe.Error;
        data["serverVersion"] = probe.ServerVersion;
        data["loadId"] = probe.LoadId;
        if (probe.Ok && probe.Rows.Count > 0)
        {
            data["next"] = probe.Rows[0]["next"];
            data["db"] = probe.Rows[0]["db"];
        }

        return new ScriptResponse { Data = data };
    }
}
