using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.DataIntegrityLab;

/// <summary>
/// data-integrity-lab: the flow built to stress instance-data writing.
/// <para>
/// lab-sequential → run-sequential (three sequential writes plus a duplicate-echo probe) →
/// lab-parallel → run-parallel (four parallel HTTP tasks) → lab-collect → auto → lab-completed
/// </para>
/// <para>
/// What matters here is not the state machine but the DATA: parallel branches write through
/// their own scopes, so a lost or duplicated write shows up as a missing key rather than as a
/// failed transition.
/// </para>
/// <para>
/// <b><c>lab-collect</c> is a fan-in gate, not a hang.</b> Its only exit is an automatic transition
/// whose rule is <c>labUpdateCount &gt;= labThreshold</c>. With the rule false the instance PARKS in
/// Busy by design (<c>ResolveAvailableStep</c>: a target with auto transitions stays Busy), and every
/// <c>update-lab-progress</c> (updateData, admitted without a lock) re-runs the auto evaluation.
/// Until 2026-10-05 the lifecycle tests never sent those updates and waited for
/// <c>lab-completed</c> — two reds recorded as "stuck Busy" for seven weeks. Verified by hand on a
/// parked instance: four updateData calls answered 202 and the instance reached lab-completed.
/// </para>
/// </summary>
public class DataIntegrityLabTests : WorkflowTestBase
{
    private const string Workflow = "data-integrity-lab";

    public DataIntegrityLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>labThreshold seeded at start — the number of updateData calls that open lab-collect.</summary>
    private const int Threshold = 3;

    private async Task<string> StartLabAsync() =>
        await StartAsync(Workflow, new { testId = $"lab-{Guid.NewGuid():N}"[..16], labThreshold = Threshold });

    /// <summary>
    /// Runs <c>run-parallel</c> and opens the <c>lab-collect</c> fan-in gate. Deliberately not
    /// <c>RunAcceptedAsync</c>: that waits for the instance to leave Busy, and <c>lab-collect</c>
    /// parks in Busy until the gate opens — so wait for the STATE, then send the updates.
    /// </summary>
    private async Task RunParallelAndOpenTheGateAsync(string id)
    {
        var status = await RunAsync(Workflow, id, "run-parallel", new { });
        Assert.True((int)status < 400, $"run-parallel was refused with {(int)status}");
        await WaitForInstanceStateAsync(Workflow, id, "lab-collect", timeout: TimeSpan.FromSeconds(60));

        await WaitUntilAsync(async () =>
        {
            if ((await GetInstanceStateAsync(Workflow, id)).State != "lab-collect") return true;
            if (await GetCounterAsync(Workflow, id, "labUpdateCount") < Threshold)
                await RunAsync(Workflow, id, "update-lab-progress",
                    new { updateNonce = Guid.NewGuid().ToString("N")[..8] });
            return false;
        }, $"lab-collect never opened after {Threshold} updateData calls", TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task FullLifecycle_ReachesLabCompleted()
    {
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");

        await RunAcceptedAsync(Workflow, id, "run-sequential", settleTimeout: TimeSpan.FromSeconds(90));
        await WaitForInstanceStateAsync(Workflow, id, "lab-parallel", timeout: TimeSpan.FromSeconds(90));

        await RunParallelAndOpenTheGateAsync(id);
        await WaitForInstanceStateAsync(Workflow, id, "lab-completed", timeout: TimeSpan.FromSeconds(90));

        var (state, status) = await GetInstanceStateAsync(Workflow, id);
        Assert.Equal("lab-completed", state);
        Assert.Equal("C", status);

        // A finished instance was last modified when it finished — not at the Busy flip that
        // started its final transition, which is what modifiedAt showed before.
        var times = await GetInstanceTimesAsync(Workflow, id);
        Assert.NotNull(times.CompletedAt);
        Assert.Equal(times.CompletedAt, times.ModifiedAt);
    }

    [Fact]
    public async Task SequentialTasks_AllLandTheirOwnKeys()
    {
        // Three sequential writers in one transition: every one of them must survive the merge.
        // A lost write here is the classic symptom of a full-echo mapping overwriting a peer.
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");

        await RunAcceptedAsync(Workflow, id, "run-sequential", settleTimeout: TimeSpan.FromSeconds(90));
        await WaitForInstanceStateAsync(Workflow, id, "lab-parallel", timeout: TimeSpan.FromSeconds(90));

        var attributes = await GetAttributesAsync(Workflow, id);
        var keys = attributes.EnumerateObject().Select(p => p.Name).ToList();

        Assert.True(keys.Count > 1,
            $"the sequential step wrote nothing beyond the start payload — {string.Join(",", keys)}");
    }

    [Fact]
    public async Task ParallelTasks_AllLandTheirOwnKeys()
    {
        // Four parallel branches each write through their own DI scope; the merge must keep all
        // four. This is the case that originally exposed the shared-DbContext write collision.
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");
        await RunAcceptedAsync(Workflow, id, "run-sequential", settleTimeout: TimeSpan.FromSeconds(90));
        await WaitForInstanceStateAsync(Workflow, id, "lab-parallel", timeout: TimeSpan.FromSeconds(90));

        var before = (await GetAttributesAsync(Workflow, id)).EnumerateObject().Count();

        await RunParallelAndOpenTheGateAsync(id);
        await WaitForInstanceStateAsync(Workflow, id, "lab-completed", timeout: TimeSpan.FromSeconds(90));

        var after = (await GetAttributesAsync(Workflow, id)).EnumerateObject().Count();

        Assert.True(after > before,
            $"the parallel step added no keys (before={before}, after={after}) — " +
            "a parallel branch's write was lost in the merge");
    }

    [Fact]
    public async Task ConcurrentUpdateData_KeepsEveryAcceptedIncrement()
    {
        // updateData is admitted unconditionally, so several can be in flight at once. Each
        // ACCEPTED one must leave exactly one increment: a lost update means two writers merged
        // against the same stale head.
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");
        await WaitUntilSettledAsync(Workflow, id);
        var beforeBurst = await GetInstanceTimesAsync(Workflow, id);

        const int target = 5;
        var accepted = 0;
        var attempts = 0;

        while (accepted < target && attempts < target * 20)
        {
            attempts++;
            var status = await RunAsync(Workflow, id, "update-lab-progress",
                new { updateNonce = Guid.NewGuid().ToString("N")[..8] });
            if ((int)status < 400) accepted++;
            await Task.Delay(150);
        }

        Assert.Equal(target, accepted);

        await WaitUntilSettledAsync(Workflow, id);
        await WaitUntilAsync(
            async () => await GetCounterAsync(Workflow, id, "labUpdateCount") >= target,
            $"labUpdateCount never reached {target} — an accepted updateData lost its increment: " +
            await DescribeAsync(Workflow, id),
            TimeSpan.FromSeconds(60));

        Assert.Equal(target, await GetCounterAsync(Workflow, id, "labUpdateCount"));

        // The burst changed data and no state: modifiedAt still moved, and the instance never left
        // lab-sequential.
        var afterBurst = await GetInstanceTimesAsync(Workflow, id);
        Assert.Equal("lab-sequential", afterBurst.State);
        Assert.True(afterBurst.ModifiedAt > beforeBurst.ModifiedAt,
            $"modifiedAt did not move over {target} updateData calls ({beforeBurst.ModifiedAt:O} → {afterBurst.ModifiedAt:O})");
    }

    [Fact]
    public async Task Cancel_MovesTheLabToCancelled()
    {
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");

        await RunAcceptedAsync(Workflow, id, "cancel-lab");
        await WaitForInstanceStateAsync(Workflow, id, "lab-cancelled", timeout: TimeSpan.FromSeconds(60));

        var (_, status) = await GetInstanceStateAsync(Workflow, id);
        Assert.True(TerminalStatuses.Contains(status), $"expected a terminal status, got {status}");

        // Cancel completes the instance: last modified at that same instant.
        var times = await GetInstanceTimesAsync(Workflow, id);
        Assert.NotNull(times.CompletedAt);
        Assert.Equal(times.CompletedAt, times.ModifiedAt);
    }

    // ── metadata.modifiedAt ──────────────────────────────────────────────────
    //
    // Before vnext feature/modified-at-fix only the Busy flips moved Instances.ModifiedAt: a finished
    // instance kept the time its final transition STARTED, and an updateData did not move it at all.

    /// <summary>
    /// The baseline: a transition that rests Active (run-sequential, lab-sequential → lab-parallel)
    /// moves modifiedAt to a time after the request.
    /// </summary>
    [Fact]
    public async Task ModifiedAt_MovesOnATransition()
    {
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");
        await WaitUntilSettledAsync(Workflow, id);
        var before = await GetInstanceTimesAsync(Workflow, id);

        await RunAcceptedAsync(Workflow, id, "run-sequential", settleTimeout: TimeSpan.FromSeconds(90));
        await WaitForInstanceStateAsync(Workflow, id, "lab-parallel", timeout: TimeSpan.FromSeconds(90));

        // Runtime clock against runtime clock only: the test host's clock may differ in external mode.
        var after = await GetInstanceTimesAsync(Workflow, id);
        Assert.True(after.ModifiedAt > before.ModifiedAt, $"{before.ModifiedAt:O} → {after.ModifiedAt:O}");
    }

    /// <summary>
    /// updateData changes data and no state: modifiedAt moves anyway — on EVERY call, not only the first.
    /// Both its request body and its onExecute task (<c>LabUpdateCounterMapping</c>, +1) write a data row.
    /// The repeat is what caught a "stamp once" shortcut (2026-11-06): consecutive hops reuse a pooled
    /// connection, and from the second call on modifiedAt stopped moving.
    /// </summary>
    [Fact]
    public async Task ModifiedAt_MovesOnUpdateData_WithoutAStateChange()
    {
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");
        await WaitUntilSettledAsync(Workflow, id);
        var before = await GetInstanceTimesAsync(Workflow, id);

        for (var call = 1; call <= 3; call++)
        {
            await SubmitAsync(Workflow, id, "update-lab-progress", new { updateNonce = Guid.NewGuid().ToString("N")[..8] });
            var expected = call;
            await WaitUntilAsync(async () => await GetCounterAsync(Workflow, id, "labUpdateCount") == expected,
                $"updateData #{call}'s increment never landed");

            var after = await GetInstanceTimesAsync(Workflow, id);
            Assert.Equal("lab-sequential", after.State);
            Assert.Equal("A", after.Status);
            Assert.True(after.ModifiedAt > before.ModifiedAt,
                $"updateData #{call} did not move modifiedAt ({before.ModifiedAt:O} → {after.ModifiedAt:O})");
            before = after;
        }
    }

    /// <summary>
    /// An updateData whose only data change is its task's output — an empty body adds nothing, so no
    /// body row is written. The task's row is the modification and must move modifiedAt; a stamp tied
    /// to the request body alone leaves it where it was.
    /// </summary>
    [Fact]
    public async Task ModifiedAt_MovesOnUpdateData_WhoseOnlyChangeIsItsTaskOutput()
    {
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");
        await WaitUntilSettledAsync(Workflow, id);
        var before = await GetInstanceTimesAsync(Workflow, id);

        await SubmitAsync(Workflow, id, "update-lab-progress", new { });
        await WaitUntilAsync(async () => await GetCounterAsync(Workflow, id, "labUpdateCount") == 1,
            "the updateData increment never landed");

        var after = await GetInstanceTimesAsync(Workflow, id);
        Assert.Equal("lab-sequential", after.State);
        Assert.True(after.ModifiedAt > before.ModifiedAt, $"{before.ModifiedAt:O} → {after.ModifiedAt:O}");
    }

    /// <summary>
    /// The list filter on <c>modifiedAt</c> sees the new value: <c>gt</c> the settled stamp matches nothing
    /// until the instance changes, and matches it once it has. The change is an updateData — data only, no
    /// state change — which the old runtime did not stamp at all, so this is red there.
    /// </summary>
    [Fact]
    public async Task ModifiedAtFilter_FindsTheInstanceOnlyAfterItChanged()
    {
        var testId = $"lab-{Guid.NewGuid():N}"[..16];
        var id = await StartAsync(Workflow, new { testId, labThreshold = Threshold });
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");
        await WaitUntilSettledAsync(Workflow, id);
        var before = await GetInstanceTimesAsync(Workflow, id);

        var filter = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["attributes"] = new { testId = new { eq = testId } },
            ["modifiedAt"] = new { gt = before.ModifiedAt.UtcDateTime.ToString("O") }
        });

        Assert.Empty(await ListIdsAsync(filter));

        await SubmitAsync(Workflow, id, "update-lab-progress", new { updateNonce = Guid.NewGuid().ToString("N")[..8] });
        await WaitUntilAsync(async () => await GetCounterAsync(Workflow, id, "labUpdateCount") == 1,
            "the updateData increment never landed");

        Assert.Equal([id], await ListIdsAsync(filter));
    }

    /// <summary>
    /// A task-free updateData: the lab's own one always writes a task-output row (+1), which would carry the
    /// stamp whatever the request changed. <c>rs-plain</c> is row-security-lab's unchanged-flow control
    /// (no <c>rowSecurity</c>, master schema requires only <c>testId</c>).
    /// </summary>
    private const string EnvelopeOnlyFlow = "rs-plain";

    /// <summary>
    /// An updateData that changes only an envelope field — tags, then stage — writes no data row
    /// (<c>entityEtag</c> stays), so no row carries the stamp: the instance's own UPDATE for the new value
    /// does. Re-sending the same tags and stage changes nothing and moves nothing — an updateData never
    /// flips Busy, so nothing else writes the instance row.
    /// </summary>
    [Fact]
    public async Task ModifiedAt_MovesOnUpdateData_ThatChangesOnlyTagsOrStage_AndNotWhenNothingChanges()
    {
        var id = await StartAsync(EnvelopeOnlyFlow, new { attributes = new { testId = $"lab-{Guid.NewGuid():N}"[..16] } });
        var started = await ReadEnvelopeAsync(id);

        await UpdateEnvelopeAsync(id, new { tags = new[] { "modified-at-lab" } });
        var tagged = await ReadEnvelopeAsync(id);
        Assert.Contains("modified-at-lab", tagged.Tags);
        Assert.Equal(started.EntityEtag, tagged.EntityEtag);
        Assert.True(tagged.ModifiedAt > started.ModifiedAt, $"tags: {started.ModifiedAt:O} → {tagged.ModifiedAt:O}");

        await UpdateEnvelopeAsync(id, new { stage = "under-review" });
        var staged = await ReadEnvelopeAsync(id);
        Assert.Equal("under-review", staged.Stage);
        Assert.Equal(started.EntityEtag, staged.EntityEtag);
        Assert.True(staged.ModifiedAt > tagged.ModifiedAt, $"stage: {tagged.ModifiedAt:O} → {staged.ModifiedAt:O}");

        await UpdateEnvelopeAsync(id, new { tags = new[] { "modified-at-lab" }, stage = "under-review" });
        var unchanged = await ReadEnvelopeAsync(id);
        Assert.Equal(staged.ModifiedAt, unchanged.ModifiedAt);
        Assert.Equal(started.EntityEtag, unchanged.EntityEtag);
        Assert.Equal(("active", "A"), (unchanged.State, unchanged.Status));
    }

    /// <summary>Runs rs-plain's updateData with <c>sync=true</c>: it answers once the hop has rested.</summary>
    private async Task UpdateEnvelopeAsync(string id, object envelope)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Patch,
            $"api/v1/core/workflows/{EnvelopeOnlyFlow}/instances/{id}/transitions/rs-update?sync=true",
            envelope, Headers());
        Assert.True((int)status < 400, $"rs-update was refused with {(int)status}: {body}");
    }

    private async Task<(DateTimeOffset ModifiedAt, string State, string Status, string? Stage, List<string> Tags, string EntityEtag)>
        ReadEnvelopeAsync(string id)
    {
        var body = (await Api.GetInstanceAsync(EnvelopeOnlyFlow, id, Headers())).Body;
        var metadata = body.GetProperty("metadata");
        Assert.True(body.TryGetProperty("entityEtag", out var entityEtag) && entityEtag.ValueKind == JsonValueKind.String,
            $"the instance read carried no entityEtag: {body}");

        return (metadata.GetProperty("modifiedAt").GetDateTimeOffset(),
                metadata.GetProperty("currentState").GetString() ?? "",
                metadata.GetProperty("status").GetString() ?? "",
                metadata.TryGetProperty("stage", out var stage) ? stage.GetString() : null,
                body.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(t => t.GetString()!).ToList() : [],
                entityEtag.GetString()!);
    }

    private async Task<List<string>> ListIdsAsync(string filter)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances?pageSize=100&filter={Uri.EscapeDataString(filter)}",
            headers: Headers());
        Assert.True((int)status < 400, $"list was refused with {(int)status}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToList();
    }
}
