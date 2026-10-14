using System.Diagnostics;
using Dapper;
using Npgsql;

namespace ScriptPluginLab.Sql;

/// <summary>
/// Result of <see cref="SqlProbe.QueryAsync"/>. Plain types only, so a mapping script needs no
/// reference to Npgsql or Dapper to read it. <see cref="Ok"/> false ⇒ <see cref="Stage"/> says where it
/// failed (open | query) and <see cref="Error"/> carries the message.
/// </summary>
public sealed class SqlProbeResult
{
    public bool Ok { get; set; }
    public string Stage { get; set; } = "open";
    public string? Error { get; set; }
    public string? ServerVersion { get; set; }
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public long ElapsedMs { get; set; }

    /// <summary>The <see cref="SqlProbe.LoadId"/> of the copy that ran this query.</summary>
    public string LoadId { get; set; } = SqlProbe.LoadId;
}

/// <summary>
/// Read-only SQL from a mapping script, through ADO.NET (Npgsql) and a third-party library the
/// runtime does not ship (Dapper) — the shape of a real database driver plugin. Never throws:
/// failures come back as <see cref="SqlProbeResult.Ok"/> = false so the script decides.
/// The connection string is the caller's; this class neither reads nor logs secrets.
/// </summary>
public static class SqlProbe
{
    /// <summary>
    /// Generated once per loaded copy of this assembly. Two scripts reporting the same value ran the
    /// same copy — the observable proof that the runtime loads a plugin once per process (one
    /// connection pool), not once per helper set.
    /// </summary>
    public static readonly string LoadId = Guid.NewGuid().ToString("N");

    public static async Task<SqlProbeResult> QueryAsync(
        string connectionString,
        string sql,
        IDictionary<string, object?>? parameters = null,
        int timeoutSeconds = 5,
        int maxRows = 100,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var result = new SqlProbeResult();
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            result.ServerVersion = connection.ServerVersion;

            result.Stage = "query";
            var command = new CommandDefinition(
                sql,
                parameters is null ? null : new DynamicParameters(parameters),
                commandTimeout: timeoutSeconds,
                cancellationToken: cancellationToken);

            foreach (var row in (await connection.QueryAsync(command)).Take(maxRows))
                result.Rows.Add(new Dictionary<string, object?>((IDictionary<string, object?>)row));

            result.Ok = true;
            result.Stage = "done";
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Error = ex.GetType().Name + ": " + ex.Message;
        }

        result.ElapsedMs = watch.ElapsedMilliseconds;
        return result;
    }
}
