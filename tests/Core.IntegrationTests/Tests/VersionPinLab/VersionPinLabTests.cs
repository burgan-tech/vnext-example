using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.VersionPinLab;

/// <summary>
/// version-pin-lab: a floating (partial) version reference — <c>"1.0"</c> — is resolved to a
/// concrete version when a child is started, and the runtime must RECORD that resolved version:
/// on the start response (<c>flowVersion</c>) and on the parent's subflow correlation
/// (<c>activeCorrelations[].subFlowVersion</c>), for both a SubFlow state and a SubProcess task.
/// <para>
/// Why the correlation must hold the resolved version: parent reads re-resolve
/// <c>SubFlowVersion</c> to find the child. Were the authored <c>"1.0"</c> stored, a later
/// <c>1.0.x</c> publish would silently redirect those reads to a child version that is NOT the one
/// the running child instance was born under.
/// </para>
/// <para>
/// Two versions of the child are published: 1.0.0 (under <c>core/</c>, auto-published by the SDK)
/// and 1.0.3, built here from the 1.0.0 file and published once through
/// <c>api/v1/definitions/publish</c>. It stays outside <c>core/</c> so the SDK publisher never posts
/// it itself; an "already published" answer on a re-run is fine (publish is version-immutable).
/// </para>
/// </summary>
public class VersionPinLabTests : WorkflowTestBase
{
    private const string Child = "version-pin-lab-child";
    private const string SubFlowParent = "version-pin-lab-subflow-parent";
    private const string SubProcessParent = "version-pin-lab-subprocess-parent";
    private const string Resolved = "1.0.3";

    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private static bool _published;

    public VersionPinLabTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task Start_ReturnsResolvedFlowVersion()
    {
        await EnsureChild103PublishedAsync();

        var (status, body) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{Child}/instances/start?version=1.0&sync=true", new { }, Headers());

        Assert.True((int)status < 400, $"start was refused with {(int)status}: {body}");
        Assert.Equal(Resolved, FlowVersionOf(JsonDocument.Parse(body).RootElement, body));
    }

    [Fact]
    public async Task SubFlowCorrelation_StoresChildResolvedVersion()
    {
        await EnsureChild103PublishedAsync();

        var parentId = await StartAsync(SubFlowParent, new { });
        var correlation = await WaitForChildCorrelationAsync(SubFlowParent, parentId);

        Assert.Equal(Resolved, correlation.GetProperty("subFlowVersion").GetString());
        await AssertChildRunsResolvedVersionAsync(correlation);

        await RunAcceptedAsync(Child, correlation.GetProperty("subFlowInstanceId").GetString()!, "finish");

        await WaitForInstanceStateAsync(SubFlowParent, parentId, "sf-completed");
        await AssertNotFaultedAsync(SubFlowParent, parentId);
    }

    [Fact]
    public async Task SubProcessCorrelation_StoresChildResolvedVersion()
    {
        await EnsureChild103PublishedAsync();

        var parentId = await StartAsync(SubProcessParent, new { });
        await WaitForInstanceStateAsync(SubProcessParent, parentId, "sp-waiting");
        var correlation = await WaitForChildCorrelationAsync(SubProcessParent, parentId);

        Assert.Equal(Resolved, correlation.GetProperty("subFlowVersion").GetString());
        await AssertChildRunsResolvedVersionAsync(correlation);

        await RunAcceptedAsync(Child, correlation.GetProperty("subFlowInstanceId").GetString()!, "finish");

        await AssertNotFaultedAsync(SubProcessParent, parentId);
        await RunAcceptedAsync(SubProcessParent, parentId, "complete");
        await WaitForInstanceStateAsync(SubProcessParent, parentId, "sp-completed");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The parent's open correlation to the child, read from the state function's
    /// <c>activeCorrelations</c> (the same surface <see cref="WorkflowTestBase.GetActiveSubflowsAsync"/> reads).
    /// </summary>
    private async Task<JsonElement> WaitForChildCorrelationAsync(string parent, string parentId)
    {
        JsonElement found = default;
        await WaitUntilAsync(async () =>
        {
            var response = await Api.CallInstanceFunctionAsync(parent, parentId, "state", headers: Headers());
            if (!response.Body.TryGetProperty("activeCorrelations", out var correlations)
                || correlations.ValueKind != JsonValueKind.Array) return false;

            foreach (var correlation in correlations.EnumerateArray())
            {
                if (correlation.TryGetProperty("subFlowName", out var name) && name.GetString() == Child)
                {
                    found = correlation.Clone();
                    return true;
                }
            }
            return false;
        }, $"{parent}/{parentId} never showed an active correlation to {Child}: {await DescribeAsync(parent, parentId)}");
        return found;
    }

    private async Task AssertChildRunsResolvedVersionAsync(JsonElement correlation)
    {
        var childId = correlation.GetProperty("subFlowInstanceId").GetString()!;
        var response = await Api.GetInstanceAsync(Child, childId, Headers());
        Assert.Equal(Resolved, FlowVersionOf(response.Body, response.Body.GetRawText()));
    }

    /// <summary>The <c>flowVersion</c> of a start response or instance read (root, else metadata).</summary>
    private static string? FlowVersionOf(JsonElement body, string raw)
    {
        if (body.TryGetProperty("flowVersion", out var top)) return top.GetString();
        if (body.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("flowVersion", out var inner))
            return inner.GetString();
        Assert.Fail($"response carried no flowVersion: {raw}");
        return null;
    }

    /// <summary>Publishes the child's 1.0.3 variant once per run; "already published" is fine.</summary>
    private async Task EnsureChild103PublishedAsync()
    {
        await PublishGate.WaitAsync();
        try
        {
            if (_published) return;

            var path = Path.Combine(RepoRoot(), "core", "Workflows", "version-pin-lab", $"{Child}.json");
            var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            definition["version"] = Resolved;
            definition["tags"]!.AsArray().Add("patch-103");
            definition["attributes"]!["labels"]!.AsArray()[0]!["label"] = "Version Pin Lab Child (1.0.3)";

            var (status, body) = await SendRawJsonAsync(
                HttpMethod.Post, "api/v1/definitions/publish", definition.ToJsonString(), Headers());

            var alreadyThere = status == HttpStatusCode.Conflict
                               || body.Contains("already", StringComparison.OrdinalIgnoreCase);
            Assert.True((int)status < 300 || alreadyThere,
                $"publishing {Child}@{Resolved} answered {(int)status}: {body}");
            _published = true;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "vnext.config.json"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("vnext.config.json not found above " + AppContext.BaseDirectory);
    }
}
