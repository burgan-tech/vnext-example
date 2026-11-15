using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ClientDisconnectLab;

/// <summary>
/// A client that disconnects in the middle of a sync request must not cancel work the runtime has
/// already committed to (vnext#1258).
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug.</b> The five HTTP actions that start a run (Start, StartSub, SubflowForward, PATCH
/// Transition, ScheduleStart) used to run the pipeline on the request's own cancellation token. A
/// client that went away — a closed tab, a client-side timeout — cancelled that token AFTER the Busy
/// reservation had been committed, so the run stopped mid-way and nothing ever released the
/// instance: it stayed Busy with no owner, and every later transition was refused with
/// <c>Instance:100031</c>.
/// </para>
/// <para>
/// <b>The fix.</b> Those actions now run detached from the client (<c>RunDetachedFromClientAsync</c>
/// on a run token that only host shutdown and the job budget cancel). After an abort the run
/// continues to its rest point and logs WorkflowLogs 10265 / 10266 with the span tag
/// <c>vnext.client.aborted=true</c>. See vnext <c>docs/runtime/status-locking.md § Client disconnect</c>.
/// </para>
/// <para>
/// <b>How the abort is made.</b> Every flow here runs one HTTP task (<c>cdl-slow-http</c>) that
/// MockLab answers after 8 s. The test sends the request with <c>sync=true</c> and cancels the
/// <see cref="HttpClient"/> call after 1 s, which closes the connection and fires the server's
/// <c>RequestAborted</c> while the task is still in flight. The test then polls the instance.
/// </para>
/// <para>
/// <b>Expected on a runtime without the fix:</b> (a), (b) and (d) red — the instance stays Busy, or
/// is left without the task result. (c) also fails at its settle wait; (e) is green on both.
/// </para>
/// </remarks>
public class ClientDisconnectLabTests : WorkflowTestBase
{
    private const string Root = "client-disconnect-lab";
    private const string Chain = "client-disconnect-lab-chain";
    private const string Parent = "client-disconnect-lab-parent";
    private const string Child = "client-disconnect-lab-child";

    private const string SlowTaskKey = "cdl-slow-http";
    private const string SlowTransition = "cdl-slow-commit";
    private const string ChildSlowTransition = "cdl-child-slow-commit";

    /// <summary>What MockLab's <c>delayMs</c> is set to in <c>client-disconnect-lab-collection.json</c>.</summary>
    private static readonly TimeSpan TaskDelay = TimeSpan.FromSeconds(8);

    /// <summary>When the client gives up. Well inside <see cref="TaskDelay"/>.</summary>
    private static readonly TimeSpan AbortAfter = TimeSpan.FromSeconds(1);

    /// <summary>How long a run that survived the abort gets to reach its rest point.</summary>
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(20);

    private readonly HttpClient _client;

    public ClientDisconnectLabTests(VNextTestEnvironment environment) : base(environment)
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>
    /// (a) A sync manual transition whose client disconnects after 1 s still runs to its target:
    /// the instance completes, the task's journal row is finished, its output landed, and nothing
    /// faulted or opened an incident.
    /// </summary>
    [Fact]
    public async Task SyncTransition_ClientAborts_RunStillCompletes()
    {
        var instanceId = await StartReadyRootAsync();

        await AssertClientAbortsAsync(HttpMethod.Patch, TransitionUrl(Root, instanceId, SlowTransition));

        await WaitForRestAsync(Root, instanceId, "cdl-done", "C");

        var rows = await GetSlowTaskRowsAsync(Root, instanceId);
        var row = Assert.Single(rows);
        Assert.Equal("completed", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, row.GetProperty("finishedAt").ValueKind);

        await AssertTaskOutputLandedAsync(Root, instanceId, SlowTransition);
        await AssertNoActiveIncidentAsync(Root, instanceId);
    }

    /// <summary>
    /// (b) A sync START whose inline automatic chain runs the slow task, aborted after 1 s. The
    /// start had already committed the instance, so the chain must still reach the rest state and
    /// park there Active — not Busy.
    /// </summary>
    /// <remarks>
    /// The abort means the start response — and with it the instance id — never arrives, so the
    /// test supplies a business <c>key</c> and finds the instance by it (GET accepts id or key).
    /// </remarks>
    [Fact]
    public async Task SyncStartWithSlowAutoChain_ClientAborts_ChainReachesRestState()
    {
        var key = $"cdl-chain-{Guid.NewGuid():N}";

        await AssertClientAbortsAsync(
            HttpMethod.Post,
            $"api/v1/{TestDomain.Name}/workflows/{Chain}/instances/start?sync=true",
            new { key, attributes = new { } });

        string? instanceId = null;
        await WaitUntilAsync(
            async () =>
            {
                var (found, id, state, status) = await TryGetByKeyAsync(Chain, key);
                if (!found) return false;
                instanceId = id;
                return state == "cdl-chain-rest" && status == "A";
            },
            $"the aborted start never reached cdl-chain-rest/A — " +
            (instanceId is null ? $"no instance with key {key}" : await DescribeAsync(Chain, instanceId)),
            SettleBudget);

        var rows = await GetSlowTaskRowsAsync(Chain, instanceId!);
        Assert.Equal("completed", Assert.Single(rows).GetProperty("status").GetString());
        await AssertTaskOutputLandedAsync(Chain, instanceId!, "cdl-chain-slow-hop");
    }

    /// <summary>
    /// (c) While the aborted run of (a) is still in flight, a second request for the same transition
    /// is refused as Busy (409 <c>Instance:100031</c>) — the detached run still owns the instance —
    /// and the task runs exactly once.
    /// </summary>
    [Fact]
    public async Task SecondRequestWhileAbortedRunInFlight_IsRefusedBusy_AndTaskRunsOnce()
    {
        var instanceId = await StartReadyRootAsync();
        var url = TransitionUrl(Root, instanceId, SlowTransition);

        await AssertClientAbortsAsync(HttpMethod.Patch, url);

        // ~1 s after the abort, ~6 s before MockLab answers: the first run is mid-task.
        await Task.Delay(TimeSpan.FromSeconds(1));

        using (var second = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(new { }) })
        {
            foreach (var (name, value) in Headers()) second.Headers.TryAddWithoutValidation(name, value);

            using var response = await _client.SendAsync(second);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.Conflict,
                $"the second request was not refused as Busy: {(int)response.StatusCode} {body}");
            Assert.Contains("Instance:100031", body, StringComparison.Ordinal);
        }

        await WaitForRestAsync(Root, instanceId, "cdl-done", "C");

        var rows = await GetSlowTaskRowsAsync(Root, instanceId);
        Assert.True(rows.Count == 1,
            $"the slow task ran {rows.Count} times; the refused request must not have run it again");
        Assert.Equal("completed", rows[0].GetProperty("status").GetString());
    }

    /// <summary>
    /// (d) The child's transition sent to the PARENT's address (the SubFlow transition proxy forwards
    /// it to the leaf) with <c>sync=true</c>, aborted after 1 s. The child must finish, the parent
    /// must resume to its own finish, and neither may be left Busy.
    /// </summary>
    /// <remarks>
    /// Core only: the SDK publisher does not rewrite the domain under <c>subFlow.process</c>, so on a
    /// side domain the parent would point at a <c>core</c> child that is not published there.
    /// </remarks>
    [SkippableFact]
    public async Task ForwardedSyncTransitionViaParent_ClientAborts_ChildAndParentReachRest()
    {
        Skip.If(TestDomain.Name != "core",
            $"subFlow.process stays domain 'core' after publish; this scenario only runs on core (VNEXT_DOMAIN={TestDomain.Name})");

        var parentId = await StartAsync(Parent, new { });

        string childId = null!;
        await WaitUntilAsync(
            async () =>
            {
                var subflows = await GetActiveSubflowsAsync(Parent, parentId);
                if (!subflows.TryGetValue(Child, out var id)) return false;
                childId = id;
                var (state, status) = await GetInstanceStateAsync(Child, id);
                return state == "cdl-child-ready" && status == "A";
            },
            $"the parent never opened a ready child — {await DescribeAsync(Parent, parentId)}");

        await AssertClientAbortsAsync(HttpMethod.Patch, TransitionUrl(Parent, parentId, ChildSlowTransition));

        await WaitForRestAsync(Child, childId, "cdl-child-done", "C");
        await WaitForRestAsync(Parent, parentId, "cdl-parent-done", "C");

        var rows = await GetSlowTaskRowsAsync(Child, childId);
        Assert.Equal("completed", Assert.Single(rows).GetProperty("status").GetString());
    }

    /// <summary>
    /// (e) Control: the same transition with a client that waits answers 200 after roughly the task
    /// delay, already at its target. Green with and without the fix; it proves the delay is real, so a
    /// green (a)–(d) is not green because MockLab answered before the abort.
    /// </summary>
    [Fact]
    public async Task SyncTransition_ClientWaits_Answers200AfterTheTask()
    {
        var instanceId = await StartReadyRootAsync();

        using var request = new HttpRequestMessage(HttpMethod.Patch, TransitionUrl(Root, instanceId, SlowTransition))
        {
            Content = JsonContent.Create(new { })
        };
        foreach (var (name, value) in Headers()) request.Headers.TryAddWithoutValidation(name, value);

        var clock = Stopwatch.StartNew();
        using var response = await _client.SendAsync(request);
        clock.Stop();
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"the waiting client got {(int)response.StatusCode}: {body}");
        Assert.True(clock.Elapsed >= TaskDelay - TimeSpan.FromMilliseconds(500),
            $"answered in {clock.Elapsed.TotalSeconds:0.0}s — MockLab's {TaskDelay.TotalSeconds:0}s delay was not applied " +
            "(seed not loaded, or another mock answers the route by prefix), so the abort tests prove nothing");
        Assert.True(clock.Elapsed < SettleBudget, $"answered only after {clock.Elapsed.TotalSeconds:0.0}s");

        var (state, status) = await GetInstanceStateAsync(Root, instanceId);
        Assert.Equal(("cdl-done", "C"), (state, status));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string TransitionUrl(string workflow, string instanceId, string transition) =>
        $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{instanceId}/transitions/{transition}?sync=true";

    private async Task<string> StartReadyRootAsync()
    {
        var instanceId = await StartAsync(Root, new { });
        await WaitForInstanceStateAsync(Root, instanceId, "cdl-ready");
        await AssertNotFaultedAsync(Root, instanceId);
        return instanceId;
    }

    /// <summary>
    /// Sends a sync request and gives up after <see cref="AbortAfter"/>. Fails the test if the
    /// runtime answered first: then the slow task never ran (or MockLab's delay is missing) and the
    /// scenario is not exercising a disconnect at all.
    /// </summary>
    private async Task AssertClientAbortsAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body ?? new { }) };
        foreach (var (name, value) in Headers()) request.Headers.TryAddWithoutValidation(name, value);

        using var abort = new CancellationTokenSource(AbortAfter);
        try
        {
            using var response = await _client.SendAsync(request, abort.Token);
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Fail($"{method} {url} answered {(int)response.StatusCode} within {AbortAfter.TotalSeconds:0}s " +
                        $"instead of running the {TaskDelay.TotalSeconds:0}s task: {responseBody}");
        }
        catch (TaskCanceledException) when (abort.IsCancellationRequested)
        {
            // The client went away while the run was in flight — the situation under test.
        }
    }

    /// <summary>Waits for an instance to rest at <paramref name="state"/> with <paramref name="status"/>.</summary>
    private async Task WaitForRestAsync(string workflow, string instanceId, string state, string status)
    {
        await WaitUntilAsync(
            async () => await GetInstanceStateAsync(workflow, instanceId) == (state, status),
            $"{workflow} did not reach {state}/{status} after the client disconnected " +
            $"(stuck Busy is the vnext#1258 symptom) — {await DescribeAsync(workflow, instanceId)}",
            SettleBudget);
    }

    /// <summary>
    /// Reads an instance by business key without failing on 404 — before the aborted start has
    /// committed, the key does not resolve yet.
    /// </summary>
    private async Task<(bool Found, string Id, string State, string Status)> TryGetByKeyAsync(string workflow, string key)
    {
        var (status, body) = await SendRawAsync(
            HttpMethod.Get, $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{key}", headers: Headers());
        if (status != HttpStatusCode.OK) return (false, "", "", "");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var metadata = root.GetProperty("metadata");
        return (true,
                root.GetProperty("id").GetString() ?? "",
                metadata.GetProperty("currentState").GetString() ?? "",
                metadata.GetProperty("status").GetString() ?? "");
    }

    /// <summary>The instance's <c>InstanceTasks</c> journal rows for the slow task (<c>functions/tasks</c>).</summary>
    private async Task<List<JsonElement>> GetSlowTaskRowsAsync(string workflow, string instanceId)
    {
        var (status, body) = await SendRawAsync(
            HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{instanceId}/functions/tasks",
            headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"functions/tasks answered {(int)status}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("taskKey").GetString() == SlowTaskKey)
            .Select(item => item.Clone())
            .ToList();
    }

    /// <summary>The slow task's OutputHandler ran and wrote its marker for <paramref name="transition"/>.</summary>
    private async Task AssertTaskOutputLandedAsync(string workflow, string instanceId, string transition)
    {
        var attributes = await GetAttributesAsync(workflow, instanceId);
        Assert.True(attributes.TryGetProperty("cdlSlowTask", out var marker),
            $"no cdlSlowTask marker — the task's output never landed. {await DescribeAsync(workflow, instanceId)}");
        Assert.Equal(transition, marker.GetProperty("transition").GetString());
    }

    private async Task AssertNoActiveIncidentAsync(string workflow, string instanceId)
    {
        var (status, body) = await SendRawAsync(
            HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{instanceId}/incidents/active",
            headers: Headers());
        Assert.True(status == HttpStatusCode.NotFound,
            $"an active incident was recorded for a run that should have finished cleanly: {(int)status} {body}");
    }
}
