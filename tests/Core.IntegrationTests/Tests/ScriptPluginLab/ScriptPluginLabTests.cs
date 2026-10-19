using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ScriptPluginLab;

/// <summary>
/// A database driver delivered as a script plugin. <c>ScriptPluginLab.Sql</c> (built from
/// <c>plugins/</c>, with its third-party dependency Dapper) sits in the runtime's
/// <c>Scripting:Sandbox:PluginDirectory</c>; two functions grant it through
/// <c>scripts.allowedAssemblies</c> and run a parameterised read-only query against PostgreSQL:
/// <list type="bullet">
/// <item><c>spl-direct-query</c> — the mapping calls the plugin directly (no helper). Before vnext's
/// plugin load context this compiled but died at run time with <c>FileNotFoundException</c>: only helper
/// sets preloaded plugins.</item>
/// <item><c>spl-helper-query</c> — the mapping reaches the plugin through the <c>spl-db-helper</c>
/// sys-mappings helper, the way a team's database helper is consumed.</item>
/// <item><c>spl-sqlserver-query</c> — the mapping uses <c>Microsoft.Data.SqlClient</c> and Dapper
/// <b>directly</b>: their own DLL chain is mounted (no wrapper assembly) and queried over plain ADO.NET,
/// Dapper and a stored procedure call against SQL Server.</item>
/// </list>
/// See README.md for the host environment they need; without it the tests skip.
/// </summary>
public sealed class ScriptPluginLabTests : IntegrationTestBase
{
    private const string DirectFunction = "spl-direct-query";
    private const string HelperFunction = "spl-helper-query";
    private const string SqlServerFunction = "spl-sqlserver-query";

    private readonly HttpClient _client;

    public ScriptPluginLabTests(VNextTestEnvironment environment) : base(environment)
    {
        _client = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    [SkippableFact]
    public async Task HelperlessMapping_QueriesPostgres_ThroughThePluginDriver()
    {
        var result = await CallAsync(DirectFunction);

        AssertQueried(result, "helperless");
    }

    [SkippableFact]
    public async Task HelperMapping_QueriesPostgres_ThroughThePluginDriver()
    {
        var result = await CallAsync(HelperFunction);

        AssertQueried(result, "helper");
    }

    /// <summary>
    /// <c>SqlProbe.LoadId</c> is generated once per loaded copy of the plugin. Both paths reporting the
    /// same value means one copy per process — one connection pool — instead of a copy per helper set.
    /// </summary>
    [SkippableFact]
    public async Task BothPaths_RunTheSameLoadedCopyOfThePlugin()
    {
        var direct = await CallAsync(DirectFunction);
        var helper = await CallAsync(HelperFunction);

        Assert.Equal(direct.GetProperty("loadId").GetString(), helper.GetProperty("loadId").GetString());
    }

    /// <summary>
    /// <c>ScriptContext.CancellationToken</c> carries the request's token, so a driver call the script
    /// makes is cancelled with the request. A context built without one would report false.
    /// </summary>
    [SkippableFact]
    public async Task ScriptContext_CarriesTheRequestCancellationToken()
    {
        var result = await CallAsync(DirectFunction);

        Assert.True(result.GetProperty("tokenCanBeCanceled").GetBoolean());
    }

    /// <summary>
    /// Plain ADO.NET from a script: <c>SqlConnection</c> + <c>SqlCommand</c> + a typed parameter. Needs
    /// the SqlClient chain to load from the plugin directory and, in the Alpine images, ICU with
    /// <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0</c> (SqlClient rejects invariant mode).
    /// </summary>
    [SkippableFact]
    public async Task SqlServer_PlainAdoNet_RunsThroughTheMountedSqlClient()
    {
        var result = await CallSqlServerAsync();

        Assert.Equal(42, result.GetProperty("adoNext").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("serverVersion").GetString()));
    }

    /// <summary>Dapper over a SqlClient connection: parameterised query read back as a row.</summary>
    [SkippableFact]
    public async Task SqlServer_Dapper_RunsAParameterisedQuery()
    {
        var result = await CallSqlServerAsync();

        Assert.Equal(42, result.GetProperty("dapperNext").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("db").GetString()));
    }

    /// <summary>
    /// A stored procedure call (<c>CommandType.StoredProcedure</c> on <c>sys.sp_executesql</c>) with typed
    /// parameters — the shape of the core-banking SP calls that motivated the scenario.
    /// </summary>
    [SkippableFact]
    public async Task SqlServer_StoredProcedure_ReturnsItsResultSet()
    {
        var result = await CallSqlServerAsync();

        Assert.Equal(42, result.GetProperty("procedureResult").GetInt32());
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static void AssertQueried(JsonElement result, string expectedPath)
    {
        Assert.Equal(expectedPath, result.GetProperty("path").GetString());
        Assert.True(result.GetProperty("ok").GetBoolean(),
            $"plugin query failed at stage '{result.GetProperty("stage")}': {result.GetProperty("error")}");
        Assert.Equal(42, result.GetProperty("next").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("db").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("serverVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("loadId").GetString()));
    }

    /// <summary>The SQL Server function's payload, asserted to have run all three calls.</summary>
    private async Task<JsonElement> CallSqlServerAsync()
    {
        var result = await CallAsync(SqlServerFunction);

        Assert.Equal("sqlserver", result.GetProperty("path").GetString());
        // "error" exists only on failure — read it lazily, or a green call throws KeyNotFoundException.
        if (!result.GetProperty("ok").GetBoolean())
            Assert.Fail($"SQL Server call failed at stage '{result.GetProperty("stage")}': " +
                        (result.TryGetProperty("error", out var error) ? error.ToString() : "(no error text)"));
        return result;
    }

    /// <summary>
    /// Calls a lab function and returns the mapping's own payload. Skips — never fails — when the
    /// environment lacks what README.md asks for: the function is unpublished (the runtime rejects a
    /// grant it cannot resolve, i.e. the plugin directory is not mounted) or the connection string is unset.
    /// </summary>
    private async Task<JsonElement> CallAsync(string functionKey)
    {
        using var response = await _client.GetAsync($"api/v1/core/functions/{functionKey}");
        var body = await response.Content.ReadAsStringAsync();

        Skip.If(response.StatusCode == HttpStatusCode.NotFound,
            $"{functionKey} is not published — start the runtime with Scripting__Sandbox__PluginDirectory " +
            "pointing at plugins/out (see Tests/ScriptPluginLab/README.md). Body: " + Trim(body));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{functionKey} → {(int)response.StatusCode}: {Trim(body)}");

        var payload = FindPayload(JsonDocument.Parse(body).RootElement.Clone())
                      ?? throw new Xunit.Sdk.XunitException($"{functionKey}: no mapping payload in {Trim(body)}");

        Skip.If(!payload.GetProperty("configured").GetBoolean(),
            $"{functionKey}: its connection string (ScriptPluginLab__ConnectionString or " +
            "ScriptPluginLab__SqlServerConnectionString) is not set on the runtime (see Tests/ScriptPluginLab/README.md).");
        return payload;
    }

    /// <summary>The object carrying the mapping's <c>path</c> field, wherever the function envelope put it.</summary>
    private static JsonElement? FindPayload(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (element.TryGetProperty("path", out _))
            return element;

        foreach (var property in element.EnumerateObject())
            if (FindPayload(property.Value) is { } found)
                return found;

        return null;
    }

    private static string Trim(string text) => text.Length <= 600 ? text : text[..600] + "…";
}
