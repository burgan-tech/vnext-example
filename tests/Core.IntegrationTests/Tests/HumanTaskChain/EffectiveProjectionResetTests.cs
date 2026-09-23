using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.HumanTaskChain;

/// <summary>
/// When a blocking SubFlow finishes, the parent's whole effective projection must come back to the
/// parent's OWN state — state key, state type AND state sub type — and the long-poll fingerprint
/// must move with it.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: <c>EffectiveStateSubType</c> had no reset writer anywhere. The SubFlow terminal
/// paths reset the state key and the status and left the type/sub-type pair behind, and the resume
/// re-enters the pipeline at <c>ClearBusyOnResumeStep</c> (order 79), past <c>ChangeStateStep</c>
/// (50) — so the resume hop never rewrites it either. A parent whose child had been in a Human
/// state therefore carried <c>subType = 6</c> permanently and was offered as an open human task to
/// every caller, forever, on an instance that had already moved on.
/// </para>
/// <para>
/// The projection is also long-poll fingerprint material, so the same defect meant a client polling
/// that parent could keep receiving <c>304 Not Modified</c> while the instance had in fact changed.
/// Both halves are asserted here: the phantom row disappears, and the poll wakes up.
/// </para>
/// </remarks>
public class EffectiveProjectionResetTests(VNextTestEnvironment environment)
    : WorkflowTestBase(environment)
{
    private const string Root = "ht-a";
    private const string ApproverRole = "ht-approver";

    /// <summary>
    /// Reads the list bypassing the response cache. Both assertions here are about a change that
    /// has just happened, and the cache's TTL is longer than any reasonable wait — polling through
    /// it would time out on a stale answer and say nothing about the projection.
    /// </summary>
    private async Task<bool> IsListedAsync(string instanceId)
    {
        var headers = Headers(ApproverRole);
        headers["X-VNext-Cache-Override"] = "true";

        var (status, body) = await SendRawAsync(
            HttpMethod.Get, "api/v1/core/functions/human-task", body: null, headers: headers);

        Assert.True(status == HttpStatusCode.OK, $"human-task function failed: {status} {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateArray().Any(row =>
            row.TryGetProperty("id", out var id) && id.GetString() == instanceId);
    }

    /// <summary>
    /// One level down — A holds B, B rests in its human state. Approving through the root forwards
    /// to the active subflow; when B completes, A must stop looking like a human task.
    /// </summary>
    [SkippableFact]
    public async Task WhenTheSubFlowCompletes_TheParentStopsBeingListedAsAHumanTask()
    {
        var instanceId = await StartAsync(Root, new
        {
            hops = 1,
            testId = Guid.NewGuid().ToString("N"),
            humanTask = new { title = "HT-A step", description = "HT-A step description" }
        }, ApproverRole);

        await AssertNotFaultedAsync(Root, instanceId, ApproverRole);
        await WaitUntilAsync(
            async () => await IsListedAsync(instanceId),
            $"{instanceId} never surfaced as a human task while its child waited",
            TimeSpan.FromMinutes(2));

        // Addressed at the root: the runtime forwards the transition to the active subflow, which
        // is the only path a client has — it does not know the child.
        await RunAcceptedAsync(Root, instanceId, "ht-b-approve", roles: ApproverRole);

        await WaitUntilAsync(
            async () => !await IsListedAsync(instanceId),
            $"{instanceId} is STILL listed after its child completed — the effective projection did "
            + "not reset, which is the phantom human task this scenario exists for",
            TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// The same moment, seen by a long-poller. The effective state and sub type are fingerprint
    /// material, so a child that finished has to move the ETag — otherwise a parked client keeps
    /// getting 304 on an instance that has changed underneath it.
    /// </summary>
    [SkippableFact]
    public async Task TheSubFlowsCompletionMovesTheParentsLongPollFingerprint()
    {
        var instanceId = await StartAsync(Root, new
        {
            hops = 1,
            testId = Guid.NewGuid().ToString("N"),
            humanTask = new { title = "HT-A step", description = "HT-A step description" }
        }, ApproverRole);

        await AssertNotFaultedAsync(Root, instanceId, ApproverRole);
        await WaitUntilAsync(
            async () => await IsListedAsync(instanceId),
            $"{instanceId} never surfaced as a human task while its child waited",
            TimeSpan.FromMinutes(2));

        var (firstStatus, etag, _) = await PollStateAsync(Root, instanceId, roles: ApproverRole);
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.False(string.IsNullOrWhiteSpace(etag), "state function returned no ETag to poll against");

        // Nothing has happened yet, so the same ETag must still be current: this proves the later
        // 200 is caused by the completion and not by an ETag that never matches anything.
        var (unchangedStatus, _, _) = await PollStateAsync(Root, instanceId, etag, ApproverRole);
        Assert.Equal(HttpStatusCode.NotModified, unchangedStatus);

        await RunAcceptedAsync(Root, instanceId, "ht-b-approve", roles: ApproverRole);

        await WaitUntilAsync(
            async () =>
            {
                var (status, _, _) = await PollStateAsync(Root, instanceId, etag, ApproverRole);
                return status == HttpStatusCode.OK;
            },
            "the parent's long-poll never woke after its child completed — the effective projection "
            + "is fingerprint material and did not move");
    }
}
