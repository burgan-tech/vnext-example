using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.HeaderExposureLab;

/// <summary>
/// <c>Workflow:HeaderExposure:Deny</c> (vnext#1260, Chair decision 2026-11-23): a listed request header is not written
/// to the transition record, is removed from transition history, and is absent from <c>context.Headers</c> in every
/// mapping — while the runtime's own reads (caller roles, identity, the forwarded credential) keep the request as it
/// arrived. Deny only; empty changes nothing.
/// <para>
/// The list is runtime configuration, so one run covers one side. <c>VNEXT_HEADER_EXPOSURE_DENY</c> tells the suite
/// what the runtime was started with: unset or empty runs the "nothing changes" tests; <c>x-exposure-probe,role</c>
/// runs the deny tests. The other side skips. README has both host commands.
/// </para>
/// </summary>
public class HeaderExposureLabTests : WorkflowTestBase
{
    private const string Workflow = "header-exposure-lab";
    private const string Probe = "x-exposure-probe";
    private const string Maker = "morph-idm.maker";
    private const string Approver = "morph-idm.approver";

    public HeaderExposureLabTests(VNextTestEnvironment environment) : base(environment) { }

    private static IReadOnlySet<string> RuntimeDenyList() =>
        (System.Environment.GetEnvironmentVariable("VNEXT_HEADER_EXPOSURE_DENY") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void SkipUnlessDenyRun() =>
        Skip.IfNot(RuntimeDenyList().SetEquals([Probe, "role"]),
            $"needs a runtime started with Workflow__HeaderExposure__Deny = {Probe}, role (VNEXT_HEADER_EXPOSURE_DENY)");

    private static void SkipUnlessDefaultRun() =>
        Skip.If(RuntimeDenyList().Count > 0, "the runtime runs with a deny list; this test pins the empty default");

    // ── empty list: nothing changes ──────────────────────────────────────────

    [SkippableFact]
    public async Task WithAnEmptyList_MappingsAndHistorySeeEveryHeader()
    {
        SkipUnlessDefaultRun();
        var instanceId = await StartAndEchoAsync("hx-default");

        var data = await GetAttributesAsync(Workflow, instanceId, Maker);
        Assert.True(data.GetProperty("startSawProbe").GetBoolean(), $"start mapping lost the probe: {data}");
        Assert.True(data.GetProperty("echoSawProbe").GetBoolean(), $"echo mapping lost the probe: {data}");
        Assert.True(data.GetProperty("echoSawRole").GetBoolean(), $"echo mapping lost the role header: {data}");

        foreach (var row in await HistoryRowsAsync(instanceId))
            Assert.Equal("probe-value", HeaderValue(row, Probe));
    }

    // ── deny list: x-exposure-probe, role ────────────────────────────────────

    [SkippableFact]
    public async Task ADeniedHeader_IsAbsentFromEveryMapping()
    {
        SkipUnlessDenyRun();
        var instanceId = await StartAndEchoAsync("hx-mapping");

        var data = await GetAttributesAsync(Workflow, instanceId, Maker);
        Assert.False(data.GetProperty("startSawProbe").GetBoolean(), $"start mapping saw a denied header: {data}");
        Assert.False(data.GetProperty("echoSawProbe").GetBoolean(), $"echo mapping saw a denied header: {data}");
        Assert.False(data.GetProperty("echoSawRole").GetBoolean(), $"echo mapping saw the denied role header: {data}");
        Assert.Contains("x-request-id", data.GetProperty("echoHeaderNames").GetString()!.Split(','));
    }

    [SkippableFact]
    public async Task ADeniedHeader_IsNotInTransitionHistory()
    {
        SkipUnlessDenyRun();
        var instanceId = await StartAndEchoAsync("hx-history");

        var rows = await HistoryRowsAsync(instanceId);
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.Null(HeaderValue(row, Probe));
            Assert.Null(HeaderValue(row, "role"));
            Assert.NotNull(HeaderValue(row, "x-request-id"));
        }
    }

    /// <summary>
    /// The runtime still resolves the caller's roles from the <c>role</c> header no mapping can see: <c>echo-headers</c>
    /// allows only the maker, and <c>authorize</c> answers per caller exactly as it would without the list.
    /// </summary>
    [SkippableFact]
    public async Task TheRuntime_StillResolvesRoles_FromADeniedHeader()
    {
        SkipUnlessDenyRun();
        var instanceId = await StartAndEchoAsync("hx-roles");

        Assert.Equal(HttpStatusCode.OK, await AuthorizeAsync(instanceId, Maker));
        Assert.Equal(HttpStatusCode.Forbidden, await AuthorizeAsync(instanceId, Approver));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, string> ProbeHeaders(string roles)
    {
        var headers = Headers(roles);
        headers[Probe] = "probe-value";
        return headers;
    }

    private async Task<string> StartAndEchoAsync(string tag)
    {
        var start = await Api.StartInstanceAsync(Workflow, new { tag = $"{tag}-{Guid.NewGuid():N}"[..20] }, ProbeHeaders(Maker));
        var instanceId = start.Body.GetProperty("id").GetString()!;
        await AssertNotFaultedAsync(Workflow, instanceId, Maker);

        var (status, raw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/echo-headers?sync=true",
            new { }, ProbeHeaders(Maker));
        Assert.True(status == HttpStatusCode.OK, $"echo-headers answered {(int)status}: {raw}");
        return instanceId;
    }

    private async Task<List<JsonElement>> HistoryRowsAsync(string instanceId)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions", headers: Headers(Maker));
        Assert.True(status == HttpStatusCode.OK, $"history answered {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("transitions").EnumerateArray().Select(r => r.Clone()).ToList();
    }

    private static string? HeaderValue(JsonElement row, string name)
    {
        if (!row.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in header.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
        }

        return null;
    }

    private async Task<HttpStatusCode> AuthorizeAsync(string instanceId, string roles)
    {
        var (status, _) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/authorize?transitionKey=echo-headers",
            headers: ProbeHeaders(roles));
        return status;
    }
}
