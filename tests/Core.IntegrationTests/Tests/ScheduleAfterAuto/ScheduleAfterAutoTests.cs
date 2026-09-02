using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ScheduleAfterAuto;

/// <summary>
/// Pipeline epilogue ordering: automatic transitions are evaluated BEFORE scheduled transitions
/// are armed.
/// <para>
/// One state (<c>gate</c>) carries both a conditional automatic transition and a scheduled one.
/// When the auto condition is satisfied the runtime sets <c>Directives.NextTransition</c> at
/// <c>RunAutomaticTransitionsStep</c> (LifecycleOrder.Auto, 80) and
/// <c>ScheduleTransitionsStep</c> (90) then arms nothing at all — no Dapr job, no
/// <c>InstanceJob</c> row, so no <c>kind: "scheduled"</c> entry in the state function. When it is
/// not satisfied the timer is armed exactly as before and fires on time.
/// </para>
/// <para>
/// <b>Why the resting state alone is not enough.</b> Under the previous ordering the timer was
/// armed first and the chained hop's <c>CancelScheduledJobsStep</c> (39) tore it down immediately,
/// so at rest both orderings look identical — only the churn differed. The auto path's onExecute
/// therefore holds the chained hop open for ~2.5s, and the test polls the state function
/// throughout: "an armed entry was never observed at any point" is a claim the old ordering would
/// have broken.
/// </para>
/// </summary>
public class ScheduleAfterAutoTests : WorkflowTestBase
{
    private const string Workflow = "schedule-after-auto";
    private const string GateState = "gate";
    private const string AdvancedState = "advanced";
    private const string TimedOutState = "gate-timedout";
    private const string ScheduledTransition = "gate-timeout";

    /// <summary>What <c>GateTimeoutTimer.csx</c> arms.</summary>
    private static readonly TimeSpan TimerDuration = TimeSpan.FromSeconds(8);

    private readonly HttpClient _raw;

    public ScheduleAfterAutoTests(VNextTestEnvironment environment) : base(environment)
    {
        // The SDK client hard-codes ?sync=true on start. The auto-winner path must be observed
        // WHILE the chain runs, which is only possible when the start returns immediately.
        _raw = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    /// <summary>
    /// Auto winner: the gate's timer is never armed, and therefore never fires.
    /// </summary>
    [Fact]
    public async Task AutoWinner_SuppressesTimerArming_AndTheTimerNeverFires()
    {
        var instanceId = await StartAsyncModeAsync("auto");

        // Poll from the moment the instance exists until the chained hop has settled in
        // 'advanced'. Any armed entry seen on the way is a failure: the auto step had already
        // picked a winner when Schedule ran.
        string? everSeenAt = null;
        var probes = 0;

        await WaitUntilAsync(
            async () =>
            {
                probes++;
                var (present, executeAt) = await ScheduledEntryAsync(instanceId);
                if (present) everSeenAt ??= executeAt ?? "(entry without executeAtUtc)";

                var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
                return state == AdvancedState && status != "B";
            },
            $"the auto winner never parked in '{AdvancedState}' — {await DescribeAsync(Workflow, instanceId)}",
            TimeSpan.FromSeconds(60));

        Assert.True(probes >= 2,
            $"only {probes} probe(s) ran — the observation window closed before it could " +
            "discriminate; check that AutoAdvanceMapping still delays the chained hop");

        Assert.True(everSeenAt is null,
            $"the gate's timer WAS armed (executeAtUtc={everSeenAt}) even though the auto step " +
            "had selected a winner — ScheduleTransitionsStep armed before/despite the NextTransition guard");

        Assert.Equal(1, await GetCounterAsync(Workflow, instanceId, "autoAdvances"));

        // Still nothing armed at rest, and nothing fires once the timer's duration has elapsed.
        Assert.False((await ScheduledEntryAsync(instanceId)).Present,
            "a scheduled entry appeared after the chain settled");

        await Task.Delay(TimerDuration + TimeSpan.FromSeconds(5));

        var (finalState, _) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal(AdvancedState, finalState);
        Assert.Equal(0, await GetCounterAsync(Workflow, instanceId, "timeoutFired"));
        Assert.False((await ScheduledEntryAsync(instanceId)).Present,
            "a scheduled entry appeared after the timer's duration elapsed");
    }

    /// <summary>
    /// No auto winner: the old behaviour is untouched — the timer is armed, exposed by the state
    /// function with its <c>executeAtUtc</c>, and fires.
    /// </summary>
    [Fact]
    public async Task NoAutoWinner_ArmsTheScheduledTransition_AndItFires()
    {
        var instanceId = await StartAsyncModeAsync("park");

        await WaitForInstanceStateAsync(Workflow, instanceId, GateState, timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(Workflow, instanceId);

        string? executeAtRaw = null;
        await WaitUntilAsync(
            async () =>
            {
                var (present, executeAt) = await ScheduledEntryAsync(instanceId);
                executeAtRaw = executeAt;
                return present;
            },
            $"no '{ScheduledTransition}' scheduled entry was exposed by the state function — " +
            $"{await DescribeAsync(Workflow, instanceId)}",
            TimeSpan.FromSeconds(20));

        Assert.NotNull(executeAtRaw);

        var executeAt = DateTimeOffset.Parse(executeAtRaw!, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);

        // The arm must be stamped from the instant the job was armed, i.e. roughly one timer
        // duration ahead of now — not epoch, not a duration-free placeholder.
        var now = DateTimeOffset.UtcNow;
        Assert.InRange(executeAt,
            now - TimerDuration,
            now + TimerDuration + TimeSpan.FromSeconds(30));

        // Generous budget: the fire goes through the Dapr scheduler, and a cold script compile on
        // the timeout hop costs a few hundred milliseconds more.
        await WaitUntilAsync(
            async () => (await GetInstanceStateAsync(Workflow, instanceId)).State == TimedOutState,
            $"the scheduled transition never fired — {await DescribeAsync(Workflow, instanceId)}",
            TimeSpan.FromSeconds(60));

        Assert.Equal(1, await GetCounterAsync(Workflow, instanceId, "timeoutFired"));
        Assert.Equal(0, await GetCounterAsync(Workflow, instanceId, "autoAdvances"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts an instance without blocking on the pipeline (<c>?sync=false</c>) so the caller can
    /// observe the epilogue while it runs.
    /// </summary>
    private async Task<string> StartAsyncModeAsync(string mode)
    {
        var url = $"api/v1/core/workflows/{Workflow}/instances/start?sync=false";
        var body = new { mode, testId = $"sched-after-auto-{Guid.NewGuid():N}"[..32] };

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);

        using var response = await _raw.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode < 400,
            $"start was refused with {(int)response.StatusCode}: {raw}");

        return JsonDocument.Parse(raw).RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("start response carried no instance id");
    }

    /// <summary>
    /// Whether the state function currently exposes the gate's scheduled entry, and its
    /// <c>executeAtUtc</c>.
    /// <para>
    /// Presence and <c>executeAtUtc</c> are reported separately on purpose:
    /// <c>WorkflowTestBase.GetScheduledExecuteAtAsync</c> returns null both when no entry is armed
    /// and when an entry is armed without that field, and this scenario asserts on the absence of
    /// the entry itself.
    /// </para>
    /// </summary>
    private async Task<(bool Present, string? ExecuteAt)> ScheduledEntryAsync(string instanceId)
    {
        var response = await Api.CallInstanceFunctionAsync(Workflow, instanceId, "state", headers: Headers());
        if (!response.Body.TryGetProperty("transitions", out var transitions)) return (false, null);

        foreach (var transition in transitions.EnumerateArray())
        {
            if (!transition.TryGetProperty("kind", out var kind) || kind.GetString() != "scheduled") continue;
            if (!transition.TryGetProperty("name", out var name) || name.GetString() != ScheduledTransition) continue;

            return (true, transition.TryGetProperty("executeAtUtc", out var at) ? at.GetString() : null);
        }

        return (false, null);
    }
}
