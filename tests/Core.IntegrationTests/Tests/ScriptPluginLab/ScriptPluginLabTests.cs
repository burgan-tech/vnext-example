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
/// </list>
/// See README.md for the host environment both need; without it every test skips.
/// </summary>
public sealed class ScriptPluginLabTests : IntegrationTestBase
{
    private const string DirectFunction = "spl-direct-query";
    private const string HelperFunction = "spl-helper-query";

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
            "ScriptPluginLab__ConnectionString is not set on the runtime (see Tests/ScriptPluginLab/README.md).");
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
