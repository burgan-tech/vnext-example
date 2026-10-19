using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Dapper;
using Microsoft.Data.SqlClient;

/// <summary>
/// script-plugin-lab — SQL Server through Microsoft.Data.SqlClient + Dapper used DIRECTLY in the script:
/// no wrapper assembly, no helper. The operator mounted the packages' own DLL chain in
/// Scripting:Sandbox:PluginDirectory; scripts.allowedAssemblies grants SqlClient, Dapper and the
/// framework's System.Data.Common. Three calls on one connection, each a shape a team would write:
/// <list type="number">
/// <item>plain ADO.NET — SqlCommand + parameter + ExecuteScalarAsync;</item>
/// <item>Dapper — a parameterised query read as a row;</item>
/// <item>a stored procedure — CommandType.StoredProcedure on sys.sp_executesql with typed parameters.</item>
/// </list>
/// All read-only. The connection string comes from host configuration
/// (ScriptPluginLab:SqlServerConnectionString); unset ⇒ configured = false and the test skips.
/// Never throws: a failure is reported with the stage it happened at, so the test shows the real error.
/// </summary>
public class SplSqlServerQueryMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = new Dictionary<string, object?> { ["path"] = "sqlserver", ["stage"] = "open" };

        var connectionString = GetConfigValue("ScriptPluginLab:SqlServerConnectionString");
        data["configured"] = !string.IsNullOrWhiteSpace(connectionString);
        if (string.IsNullOrWhiteSpace(connectionString))
            return new ScriptResponse { Data = data };

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(context.CancellationToken);
            data["serverVersion"] = connection.ServerVersion;

            data["stage"] = "ado";
            await using (var command = new SqlCommand("select @n + 1", connection))
            {
                command.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = 41 });
                data["adoNext"] = Convert.ToInt32(await command.ExecuteScalarAsync(context.CancellationToken));
            }

            data["stage"] = "dapper";
            var row = (IDictionary<string, object>)await connection.QueryFirstAsync(new CommandDefinition(
                "select @n + 1 as next, db_name() as db",
                new { n = 41 },
                commandTimeout: 5,
                cancellationToken: context.CancellationToken));
            data["dapperNext"] = row["next"];
            data["db"] = row["db"];

            data["stage"] = "procedure";
            var procedure = new DynamicParameters();
            procedure.Add("@stmt", "select @x * 2 as doubled", DbType.String);
            procedure.Add("@params", "@x int", DbType.String);
            procedure.Add("@x", 21, DbType.Int32);
            data["procedureResult"] = (await connection.QueryAsync<int>(new CommandDefinition(
                "sys.sp_executesql",
                procedure,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 5,
                cancellationToken: context.CancellationToken))).Single();

            data["stage"] = "done";
            data["ok"] = true;
        }
        catch (Exception ex)
        {
            data["ok"] = false;
            data["error"] = ex.GetType().Name + ": " + ex.Message;
        }

        return new ScriptResponse { Data = data };
    }
}
