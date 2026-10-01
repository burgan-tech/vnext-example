using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// What a fault deep inside a SubFlow chain leaves behind at each level, when the ROOT handles it.
/// <c>eb-sf-root</c> → <c>eb-sf-mid</c> → <c>eb-sf-leaf</c>: the leaf's HTTP task answers 400 and
/// its global boundary aborts; the mid only has a global abort, so the propagated fault faults it
/// too; the root's global boundary is a <c>notify</c> with a transition, so the root routes to
/// <c>r-error-end</c> and COMPLETES.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Reported from preprod (onboarding <c>kyc-main-workflow</c>, instance
/// <c>c831d3f0…</c>, runtime 0.0.93): "an HTTP task returned 400 in a subflow, the main flow ended
/// in error-end, and the main flow has no incident". The chain here is the same shape, level for
/// level, as the traces of that instance.
/// </para>
/// <para>
/// <b>What was measured.</b> The root's incident is NOT missing. <c>SubflowFaultService</c> records
/// it on the parent (with the boundary's verdict, <c>Notify</c>/<c>Global</c>, and the leaf's error
/// code and status) before it runs the boundary transition, and <c>FinalizeTransitionStep</c>
/// resolves every open incident when an error-boundary transition completes without faulting —
/// the same rule that closes a task-level rollback/notify. So the root reports
/// <c>hasActiveIncident: false</c>, no <c>incident.active</c> link and a 404 from
/// <c>/incidents/active</c>, while its history holds one RESOLVED row. The two levels that did
/// fault keep their incident open. These tests pin that behaviour; if the product decides a handled
/// subflow fault should stay active on the root, they are the ones to change.
/// </para>
/// </remarks>
public class SubFlowFaultChainTests : ErrorBoundaryLabTestBase
{
    private const string Root = "eb-sf-root";
    private const string Mid = "eb-sf-mid";
    private const string Leaf = "eb-sf-leaf";

    private readonly MockLabAdminClient _mockLab = new();

    public SubFlowFaultChainTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task TheRootHandlesTheFault_AndEndsInItsErrorState()
    {
        var chain = await RunChainAsync();
        if (chain is null) return;

        var (state, status) = await GetInstanceStateAsync(Root, chain.Value.Root);
        Assert.Equal("r-error-end", state);
        Assert.Equal("C", status);
    }

    [Fact]
    public async Task TheRootRecordsTheSubFlowFault_AsAResolvedIncident()
    {
        var chain = await RunChainAsync();
        if (chain is null) return;

        var items = await GetIncidentItemsAsync(Root, chain.Value.Root);
        var incident = Assert.Single(items);

        // The leaf's failure travels up verbatim: task, status and the chained code.
        Assert.Equal("SubFlow", Text(incident, "errorLayer"));
        Assert.Equal("eb-http-400-task", Text(incident, "task"));
        Assert.Equal(400, Number(incident, "statusCode"));
        Assert.EndsWith("Task:Http:eb-http-400-task:400", Text(incident, "errorCode"));
        Assert.Equal("r-in-mid", Text(incident, "state"));

        // The root's own verdict, not the children's abort.
        Assert.Equal("Notify", Text(incident, "boundaryAction"));
        Assert.Equal("Global", Text(incident, "boundaryLevel"));

        // Resolved by FinalizeTransitionStep when the boundary transition completed.
        Assert.True(Flag(incident, "isResolved"), $"the root's incident is still open: {Summarize(incident)}");
    }

    /// <summary>
    /// What a mapping running INSIDE the root's error-boundary handling reads from
    /// <c>context.Incident</c>: the boundary transition's own task (<c>probeTransition</c>) and the
    /// error end's OnEntry (<c>probeEntry</c>), both before <c>FinalizeTransitionStep</c> resolves
    /// the incident. Before the fix both saw <c>hasActiveIncident: true</c> with no active incident
    /// and a count of 0 — the script context read the snapshot's EF navigation, which is empty on a
    /// snapshot.
    /// </summary>
    [Theory]
    [InlineData("probeTransition")]
    [InlineData("probeEntry")]
    public async Task AMappingInsideTheBoundaryHandling_SeesTheSubFlowIncident(string slot)
    {
        var chain = await RunChainAsync();
        if (chain is null) return;

        var attributes = await GetAttributesAsync(Root, chain.Value.Root);
        Assert.True(attributes.TryGetProperty(slot, out var probe), $"the {slot} mapping never ran: {attributes}");

        Assert.True(Flag(probe, "hasActiveIncident"), $"{slot}: {probe}");
        Assert.True(Flag(probe, "activeIncidentPresent"), $"{slot} saw no active incident: {probe}");
        Assert.Equal(1, Number(probe, "totalIncidentCount"));
        Assert.Equal("eb-http-400-task", Text(probe, "activeTask"));
        Assert.Equal(400, Number(probe, "activeStatusCode"));
        Assert.Equal("Notify", Text(probe, "activeBoundaryAction"));
        Assert.EndsWith("Task:Http:eb-http-400-task:400", Text(probe, "activeErrorCode"));
    }

    [Fact]
    public async Task TheRootReportsNoActiveIncident_OnEverySurface()
    {
        var chain = await RunChainAsync();
        if (chain is null) return;

        var metadata = await GetIncidentMetadataAsync(Root, chain.Value.Root);
        Assert.NotNull(metadata);
        Assert.False(Flag(metadata!.Value, "hasActiveIncident"));
        AssertNoActiveLink(metadata.Value);

        var stateBlock = await GetStateIncidentAsync(Root, chain.Value.Root);
        Assert.False(Flag(stateBlock, "hasActiveIncident"));
        AssertNoActiveLink(stateBlock);

        var (status, _) = await GetActiveIncidentAsync(Root, chain.Value.Root);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Theory]
    [InlineData(Mid)]
    [InlineData(Leaf)]
    public async Task TheLevelsThatFaulted_KeepTheirIncidentOpen(string workflow)
    {
        var chain = await RunChainAsync();
        if (chain is null) return;

        var instanceId = workflow == Mid ? chain.Value.Mid : chain.Value.Leaf;
        var (_, instanceStatus) = await GetInstanceStateAsync(workflow, instanceId);
        Assert.Equal("F", instanceStatus);

        var incident = Assert.Single(await GetIncidentItemsAsync(workflow, instanceId));
        Assert.Equal("Abort", Text(incident, "boundaryAction"));
        Assert.Equal(400, Number(incident, "statusCode"));
        Assert.False(Flag(incident, "isResolved"), $"{workflow}'s incident was resolved: {Summarize(incident)}");

        var (activeStatus, active) = await GetActiveIncidentAsync(workflow, instanceId);
        Assert.Equal(HttpStatusCode.OK, activeStatus);
        Assert.Equal(Text(incident, "id"), Text(active, "id"));
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the root, waits for it to finish and resolves the two child instance ids through the
    /// <c>testId</c> the subflow mapping hands down. Null (the test returns early) when MockLab or
    /// the 400 route is not there — the chain cannot fail without it.
    /// </summary>
    private async Task<(string Root, string Mid, string Leaf)?> RunChainAsync()
    {
        if (!await _mockLab.IsUpAsync() || await _mockLab.FindMockIdAsync("api/eb-lab/fail-400") is null)
        {
            return null;
        }

        var testId = $"sf-chain-{Guid.NewGuid():N}";
        var rootId = await StartAsync(Root, new { testId });

        await WaitUntilAsync(
            async () => (await GetInstanceStateAsync(Root, rootId)).Status is "C" or "F",
            $"{Root}/{rootId} never reached a terminal status — {await DescribeAsync(Root, rootId)}",
            TimeSpan.FromSeconds(30));

        var midId = await FindByTestIdAsync(Mid, testId);
        var leafId = await FindByTestIdAsync(Leaf, testId);
        return (rootId, midId, leafId);
    }

    private async Task<string> FindByTestIdAsync(string workflow, string testId)
    {
        var filter = Uri.EscapeDataString("""{"attributes":{"testId":{"eq":""" + JsonSerializer.Serialize(testId) + "}}}");
        var (status, body) = await SendRawAsync(
            HttpMethod.Get, $"api/v1/core/workflows/{workflow}/instances?filter={filter}&pageSize=10",
            headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"listing {workflow} failed with {(int)status}: {body}");

        using var document = JsonDocument.Parse(body);
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        return item.GetProperty("id").GetString()!;
    }

    private static void AssertNoActiveLink(JsonElement incidentBlock) =>
        Assert.True(!incidentBlock.TryGetProperty("active", out var active) || active.ValueKind == JsonValueKind.Null,
            "incident.active is advertised on a root whose only incident is resolved");
}
