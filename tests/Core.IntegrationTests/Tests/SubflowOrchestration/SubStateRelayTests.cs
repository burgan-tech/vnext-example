using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SubflowOrchestration;

/// <summary>
/// The parent's <c>effectiveState</c> — the state a descendant is actually resting in, carried up
/// the whole ancestor chain — and the ordering guard that protects it.
/// <para>
/// A child's state change reaches its parent as a distributed <c>InstanceSubStateChangedEvent</c>.
/// Since the post-commit relay work it ALSO travels as an immediate command, and the receiving
/// service (<c>SubflowStateService</c>) hands the event it raises for the NEXT level up back to the
/// same relay — so the fast path walks parent ← child ← grandchild rather than stopping one level
/// up. Because both delivery paths land in the same service, that service now takes the same
/// per-sub-item lock the three terminal paths take, and keeps its monotonic
/// <c>SubFlowStateChangedAt</c> guard so the duplicate is order-safe.
/// </para>
/// <para>
/// These tests assert the guarantees, not the latency: propagation SPEED is measured separately by
/// <c>api-tests/subflow-orchestration/substate-relay-latency.py</c>, because a single-sample timing
/// assertion in a functional test is noise, not evidence.
/// </para>
/// </summary>
public class SubStateRelayTests : WorkflowTestBase
{
    private const string Parent = "subflow-orchestration-parent";
    private const string Child = "subflow-orchestration-child";
    private const int Threshold = 3;

    public SubStateRelayTests(VNextTestEnvironment environment) : base(environment) { }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The parent's own metadata block, which carries both states.</summary>
    private async Task<JsonElement> GetMetadataAsync(string instanceId)
    {
        var response = await Api.GetInstanceAsync(Parent, instanceId, Headers());
        return response.Body.GetProperty("metadata");
    }

    private async Task<string> GetEffectiveStateAsync(string instanceId) =>
        (await GetMetadataAsync(instanceId)).GetProperty("effectiveState").GetString() ?? "";

    /// <summary>
    /// Starts a parent and pushes updateData until the collect gate fires — the same drive the
    /// SubflowOrchestrationTests suite uses, repeated here so this class stands alone.
    /// </summary>
    private async Task<string> StartAndOpenTheGateAsync(string tag)
    {
        var parentId = await StartAsync(Parent,
            new { testId = $"{tag}-{Guid.NewGuid():N}"[..24], updateThreshold = Threshold });

        // parent-collect parks Busy at rest — it has an auto transition — so wait on the state.
        await WaitForInstanceStateAsync(Parent, parentId, "parent-collect");

        var accepted = 0;
        await WaitUntilAsync(async () =>
        {
            if (accepted < Threshold)
            {
                var status = await RunAsync(Parent, parentId, "update-parent-progress",
                    new { updateNonce = Guid.NewGuid().ToString("N")[..8] });
                if ((int)status < 400) accepted++;
                await Task.Delay(200);
            }

            return (await GetInstanceStateAsync(Parent, parentId)).State == "parent-subflow-state";
        }, $"the collect gate never fired after {Threshold} accepted updates", TimeSpan.FromSeconds(90));

        return parentId;
    }

    private async Task WaitForEffectiveStateAsync(
        string parentId, string expected, TimeSpan? timeout = null) =>
        await WaitUntilAsync(
            async () => await GetEffectiveStateAsync(parentId) == expected,
            $"the parent's effectiveState never became '{expected}' " +
            $"(last seen: '{await GetEffectiveStateAsync(parentId)}')",
            timeout ?? TimeSpan.FromSeconds(60));

    /// <summary>The active child's instance id, read from the parent's state function.</summary>
    private async Task<string> GetChildIdAsync(string parentId)
    {
        var subflows = await GetActiveSubflowsAsync(Parent, parentId);
        Assert.True(subflows.ContainsKey(Child), "the child subflow was not started");
        return subflows[Child];
    }

    /// <summary>
    /// Posts a <c>sub/state</c> command directly — the same internal endpoint both the post-commit
    /// relay and the Inbox backup call. Driving it by hand is the only way to control
    /// <c>changedAt</c>, which is what the ordering guard compares on.
    /// </summary>
    private Task<(HttpStatusCode Status, string Body)> PostSubStateAsync(
        string parentId, string childId, string newState, DateTime changedAtUtc) =>
        SendRawAsync(
            HttpMethod.Post,
            $"api/v1/core/workflows/{Parent}/instances/{parentId}/sub/state",
            new
            {
                parentInstanceId = parentId,
                subInstanceId = childId,
                domain = "core",
                flow = Parent,
                version = (string?)null,
                newState,
                previousState = "child-manual-state",
                newStateType = "intermediate",
                newStateSubType = "none",
                changedAt = changedAtUtc.ToString("O")
            },
            Headers());

    // ── tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// One level: while the parent rests in <c>parent-subflow-state</c>, its effectiveState is the
    /// CHILD's state. currentState and effectiveState are different fields and must not be confused —
    /// the parent never leaves its own SubFlow state.
    /// </summary>
    [Fact]
    public async Task EffectiveState_FollowsTheActiveChild_WhileTheParentStaysInItsOwnState()
    {
        var parentId = await StartAndOpenTheGateAsync("depth1");
        await WaitForObservedStateAsync(Parent, parentId, "child-manual-state");

        await WaitForEffectiveStateAsync(parentId, "child-manual-state");

        var metadata = await GetMetadataAsync(parentId);
        Assert.Equal("parent-subflow-state", metadata.GetProperty("currentState").GetString());
        Assert.Equal("child-manual-state", metadata.GetProperty("effectiveState").GetString());
    }

    /// <summary>
    /// THE depth ≥ 2 acceptance test. The grandchild's state has to cross TWO propagation hops to
    /// reach the parent: grandchild → child (the child is itself a subflow, so applying the change
    /// raises its own upward event) → parent. Before the second relay call site existed, only the
    /// first hop took the fast path and everything above it waited on the broker.
    /// </summary>
    [Fact]
    public async Task EffectiveState_FollowsTheGrandchild_AcrossTwoLevels()
    {
        var parentId = await StartAndOpenTheGateAsync("depth2");
        await WaitForObservedStateAsync(Parent, parentId, "child-manual-state");

        await RunAsync(Parent, parentId, "proceed-to-subflow");
        await WaitForObservedStateAsync(Parent, parentId, "grandchild-initial", timeout: TimeSpan.FromSeconds(60));

        // The grandchild's state, on the PARENT, two levels up.
        await WaitForEffectiveStateAsync(parentId, "grandchild-initial");

        var metadata = await GetMetadataAsync(parentId);
        Assert.Equal("parent-subflow-state", metadata.GetProperty("currentState").GetString());
        Assert.Equal("grandchild-initial", metadata.GetProperty("effectiveState").GetString());
    }

    /// <summary>
    /// The named production consumer of effectiveState: the instance query's <c>state</c> field is an
    /// alias for it (<c>InstanceFieldDiscriminator</c>), so a client filtering on <c>state</c> finds a
    /// parent by where its chain actually is — not by the parent's own state. This is what makes the
    /// freshness of effectiveState externally observable at all.
    /// </summary>
    [Fact]
    public async Task TheStateQueryAlias_FindsTheParentByItsEffectiveState()
    {
        var parentId = await StartAndOpenTheGateAsync("alias");
        await WaitForEffectiveStateAsync(parentId, "child-manual-state");

        var filter = Uri.EscapeDataString("""{"state":{"eq":"child-manual-state"}}""");
        var (status, body) = await SendRawAsync(
            HttpMethod.Get,
            $"api/v1/core/workflows/{Parent}/instances?filter={filter}&pageSize=100",
            headers: Headers());

        Assert.True((int)status < 400, $"the state-alias query failed with {(int)status}: {body}");

        using var document = JsonDocument.Parse(body);
        var ids = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToList();

        Assert.Contains(parentId, ids);

        // …and the alias really resolved to effectiveState, not to currentState.
        var mine = document.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == parentId);
        Assert.Equal("child-manual-state", mine.GetProperty("metadata").GetProperty("effectiveState").GetString());
        Assert.Equal("parent-subflow-state", mine.GetProperty("metadata").GetProperty("currentState").GetString());
    }

    /// <summary>
    /// The ordering guard. Both delivery paths (relay and Inbox backup) carry the same event, so the
    /// loser of a duplicate is routine — and a late one must never move the parent BACKWARDS. A
    /// delivery whose <c>changedAt</c> is older than what the correlation already recorded is
    /// rejected; the endpoint still answers 200, because a stale duplicate is a normal outcome and
    /// not an error the caller should retry.
    /// </summary>
    [Fact]
    public async Task AStaleSubStateDelivery_DoesNotDowngradeTheParent()
    {
        var parentId = await StartAndOpenTheGateAsync("stale");
        await WaitForObservedStateAsync(Parent, parentId, "child-manual-state");
        await WaitForEffectiveStateAsync(parentId, "child-manual-state");

        var childId = await GetChildIdAsync(parentId);

        var (status, body) = await PostSubStateAsync(
            parentId, childId, "relay-stale-must-not-land", DateTime.UtcNow.AddMinutes(-10));

        Assert.True((int)status < 400, $"sub/state was refused with {(int)status}: {body}");

        // Give the write a chance to land if the guard were broken, then assert it did not.
        await Task.Delay(2000);
        Assert.Equal("child-manual-state", await GetEffectiveStateAsync(parentId));
    }

    /// <summary>
    /// The other half of the guard: it must reject only what is actually older. A delivery with a
    /// NEWER <c>changedAt</c> is applied — otherwise the guard would be silently swallowing real
    /// progress, which a stale-only test would never catch.
    /// </summary>
    [Fact]
    public async Task AFreshSubStateDelivery_IsApplied()
    {
        var parentId = await StartAndOpenTheGateAsync("fresh");
        await WaitForObservedStateAsync(Parent, parentId, "child-manual-state");
        await WaitForEffectiveStateAsync(parentId, "child-manual-state");

        var childId = await GetChildIdAsync(parentId);
        const string marker = "relay-fresh-must-land";

        var (status, body) = await PostSubStateAsync(
            parentId, childId, marker, DateTime.UtcNow.AddSeconds(5));

        Assert.True((int)status < 400, $"sub/state was refused with {(int)status}: {body}");

        await WaitForEffectiveStateAsync(parentId, marker, TimeSpan.FromSeconds(30));
    }
}
