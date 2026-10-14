using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScriptPluginLab.Sql;

namespace ScriptPluginLab.Helpers;

/// <summary>
/// script-plugin-lab — sys-mappings helper that wraps the plugin DLL (ScriptPluginLab.Sql). Same shape
/// as a team's "database helper": the SQL lives here, consumers only call <see cref="SplDb.NextAsync"/>
/// and never touch the driver types. A consumer declares this helper in scripts.helpers and grants
/// ScriptPluginLab.Sql in scripts.allowedAssemblies (the helper compiles under the consumer's grant).
/// </summary>
public static class SplDb
{
    public static async Task<Dictionary<string, object?>> NextAsync(
        string connectionString, int n, CancellationToken cancellationToken)
    {
        var probe = await SqlProbe.QueryAsync(
            connectionString,
            "select @n::int + 1 as next, current_database() as db",
            new Dictionary<string, object?> { ["n"] = n },
            cancellationToken: cancellationToken);

        var data = new Dictionary<string, object?>
        {
            ["ok"] = probe.Ok,
            ["stage"] = probe.Stage,
            ["error"] = probe.Error,
            ["serverVersion"] = probe.ServerVersion,
            ["loadId"] = probe.LoadId
        };
        if (probe.Ok && probe.Rows.Count > 0)
        {
            data["next"] = probe.Rows[0]["next"];
            data["db"] = probe.Rows[0]["db"];
        }

        return data;
    }
}
