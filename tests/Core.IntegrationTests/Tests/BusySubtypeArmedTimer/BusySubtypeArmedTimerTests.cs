using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.BusySubtypeArmedTimer;

/// <summary>
/// Interaction wait: a Busy-SUBTYPE state (<c>subType: 5</c>) that declares an <c>interaction</c>.
/// <para>
/// The author parks the instance Busy on purpose to steer the client, and waits for an outside
/// trigger — an updateData whose data opens the state's automatic transition. Nobody owns that Busy.
/// Two ways out, both broken until vnext's fix:
/// </para>
/// <list type="bullet">
/// <item><b>The trigger arrives.</b> updateData's continuation has to take the Busy over. The wait
/// state's own ARMED timeout counted as the Busy's owner, so the continuation was dropped
/// (<c>Instance:100031</c>). Measured on QA digital-investment 2026-10-20: updateData two seconds after
/// the state was entered, timeout armed for two hours.</item>
/// <item><b>The trigger never arrives.</b> The timeout fires and was refused as busy, which consumed
/// the timer and left the instance parked for good.</item>
/// </list>
/// <para>
/// <c>route</c> picks the wait variant with a manual transition: <c>contract-wait</c> (interaction that
/// keeps polling, the QA shape), <c>ack-wait</c> (terminating interaction, timer armed after the
/// acknowledge), <c>plain-wait</c> (Busy subtype WITHOUT interaction — the timer keeps the old rule).
/// </para>
/// </summary>
public class BusySubtypeArmedTimerTests : WorkflowTestBase
{
    private const string Workflow = "busy-subtype-armed-timer";
    private const string SignedState = "contract-signed";
    private const string TimedOutState = "contract-timedout";
    private const string UpdateData = "update-contract-data";

    private static readonly Variant Interaction = new("go-interaction", "contract-wait", "contract-timeout");
    private static readonly Variant Ack = new("go-ack", "ack-wait", "ack-timeout");
    private static readonly Variant Plain = new("go-plain", "plain-wait", "plain-timeout");

    public BusySubtypeArmedTimerTests(VNextTestEnvironment environment) : base(environment)
    {
    }

    /// <summary>
    /// The QA reproduction: the gate opens long before the timer is due, the instance advances
    /// through it, and the timer it left behind is cancelled and never fires.
    /// </summary>
    [Fact]
    public async Task UpdateData_InInteractionWait_AdvancesThroughTheAutoGate_BeforeTheTimer()
    {
        var timer = TimeSpan.FromSeconds(30);
        var instanceId = await EnterWaitAsync(Interaction, timer);

        await SubmitAsync(Workflow, instanceId, UpdateData, new { contractReady = true });

        // Well under the timer: reaching 'contract-signed' here can only be the gate.
        await WaitForInstanceStateAsync(Workflow, instanceId, SignedState, timeout: TimeSpan.FromSeconds(20));
        await WaitUntilSettledAsync(Workflow, instanceId);

        Assert.Equal(1, await GetCounterAsync(Workflow, instanceId, "readyAdvances"));
        Assert.Equal(0, await GetCounterAsync(Workflow, instanceId, "timeoutFired"));
        Assert.Equal("A", (await GetInstanceStateAsync(Workflow, instanceId)).Status);
        Assert.False(await HasScheduledEntryAsync(instanceId, Interaction.Timer),
            "the wait state's timer is still armed after the instance left the state");

        // The cancelled timer stays cancelled past its fire time.
        await Task.Delay(timer + TimeSpan.FromSeconds(5));
        Assert.Equal(SignedState, (await GetInstanceStateAsync(Workflow, instanceId)).State);
        Assert.Equal(0, await GetCounterAsync(Workflow, instanceId, "timeoutFired"));
    }

    /// <summary>
    /// An updateData that does NOT open the gate changes nothing about the wait: still Busy, timer
    /// still armed.
    /// </summary>
    [Fact]
    public async Task UpdateData_ThatLeavesTheGateClosed_KeepsTheInstanceWaitingWithItsTimer()
    {
        var instanceId = await EnterWaitAsync(Interaction, TimeSpan.FromSeconds(60));

        await SubmitAsync(Workflow, instanceId, UpdateData, new { contractReady = false });

        // updateData runs as a background job; give it time to have run before asserting nothing moved.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await AssertNotFaultedAsync(Workflow, instanceId);

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal(Interaction.State, state);
        Assert.Equal("B", status);
        Assert.Equal(0, await GetCounterAsync(Workflow, instanceId, "readyAdvances"));
        Assert.True(await HasScheduledEntryAsync(instanceId, Interaction.Timer),
            "the timer was disarmed by an updateData that opened nothing");
    }

    /// <summary>
    /// The way out when the trigger never comes: the timer takes the parked Busy over and moves the
    /// instance out of the interaction wait.
    /// </summary>
    [Fact]
    public async Task Timer_InInteractionWait_TakesTheInstanceOut_WhenNoUpdateDataArrives()
    {
        var instanceId = await EnterWaitAsync(Interaction, TimeSpan.FromSeconds(6));

        await WaitUntilAsync(
            async () => (await GetInstanceStateAsync(Workflow, instanceId)).State == TimedOutState,
            $"the timeout never took the instance out of '{Interaction.State}' — {await DescribeAsync(Workflow, instanceId)}",
            TimeSpan.FromSeconds(60));

        Assert.Equal(1, await GetCounterAsync(Workflow, instanceId, "timeoutFired"));
        Assert.Equal(0, await GetCounterAsync(Workflow, instanceId, "readyAdvances"));
        Assert.Equal("C", (await GetInstanceStateAsync(Workflow, instanceId)).Status);
    }

    /// <summary>
    /// Terminating interaction: the pipeline pauses for the client's acknowledge and arms no timer
    /// before it. After the acknowledge the instance still rests Busy (Busy subtype) with the timer
    /// armed — and the timer takes it out.
    /// </summary>
    [Fact]
    public async Task Timer_AfterTheLongPollAck_InInteractionWait_TakesTheInstanceOut()
    {
        // Long enough to observe the armed timer and the Busy rest before it fires.
        var instanceId = await EnterAsync(Ack, TimeSpan.FromSeconds(15));

        // Paused at the interaction: no timer yet.
        Assert.False(await HasScheduledEntryAsync(instanceId, Ack.Timer),
            "the timer was armed before the acknowledge — the pause skips the epilogue");

        var (ackStatus, ackBody) = await SendRawAsync(
            HttpMethod.Post, $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/longpoll/ack",
            headers: Headers());
        Assert.True(ackStatus is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.Accepted,
            $"acknowledge was refused with {(int)ackStatus}: {ackBody}");

        var ackedAt = DateTimeOffset.UtcNow;

        // Resumed, and still resting Busy (Busy subtype). The armed timer is not observed through the
        // state function: neither state nor status changed, and job-set changes are outside the state
        // fingerprint (vnext #864), so a cached body would not list it.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var (restState, restStatus) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal(Ack.State, restState);
        Assert.Equal("B", restStatus);

        await WaitUntilAsync(
            async () => (await GetInstanceStateAsync(Workflow, instanceId)).State == TimedOutState,
            $"the timeout never took the instance out of '{Ack.State}' — {await DescribeAsync(Workflow, instanceId)}",
            TimeSpan.FromSeconds(60));
        Assert.Equal(1, await GetCounterAsync(Workflow, instanceId, "timeoutFired"));

        // The timer (15 s, armed at the acknowledge) did it — not the 120 s acknowledge fallback.
        var elapsed = DateTimeOffset.UtcNow - ackedAt;
        Assert.True(elapsed >= TimeSpan.FromSeconds(13),
            $"left the wait {elapsed.TotalSeconds:F1}s after the acknowledge — sooner than the timer");
    }

    /// <summary>
    /// Scope boundary: a Busy-subtype state WITHOUT interaction keeps the old rule — its timer is
    /// refused as busy and the instance stays parked. Pins that the takeover is scoped to interaction
    /// waits; flip this test deliberately if that scope is widened.
    /// </summary>
    [Fact]
    public async Task Timer_InBusySubtypeWithoutInteraction_IsRefused_AndTheInstanceStaysParked()
    {
        var timer = TimeSpan.FromSeconds(6);
        var instanceId = await EnterWaitAsync(Plain, timer);

        // Past the fire time with room for the scheduler and a cold compile. Wait on the clock, not
        // on the scheduled entry: job-set changes are outside the state fingerprint (vnext #864), so
        // a cached body may keep listing the consumed timer.
        await Task.Delay(timer + TimeSpan.FromSeconds(10));

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal(Plain.State, state);
        Assert.Equal("B", status);
        Assert.Equal(0, await GetCounterAsync(Workflow, instanceId, "timeoutFired"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Variant(string Go, string State, string Timer);

    /// <summary>Starts an instance and moves it into <paramref name="variant"/>'s wait state.</summary>
    private async Task<string> EnterAsync(Variant variant, TimeSpan timer)
    {
        var instanceId = await StartAsync(Workflow, new
        {
            timerSeconds = (int)timer.TotalSeconds,
            testId = $"bsat-{Guid.NewGuid():N}"[..32]
        });
        await WaitForInstanceStateAsync(Workflow, instanceId, "route", timeout: TimeSpan.FromSeconds(30));
        await WaitUntilSettledAsync(Workflow, instanceId);

        await SubmitAsync(Workflow, instanceId, variant.Go);
        await WaitForInstanceStateAsync(Workflow, instanceId, variant.State, timeout: TimeSpan.FromSeconds(30));
        await AssertNotFaultedAsync(Workflow, instanceId);
        return instanceId;
    }

    /// <summary>
    /// <see cref="EnterAsync"/> for a wait that does not pause: the instance rests Busy (Busy subtype)
    /// and the timer gets armed.
    /// </summary>
    private async Task<string> EnterWaitAsync(Variant variant, TimeSpan timer)
    {
        var instanceId = await EnterAsync(variant, timer);

        await WaitUntilAsync(
            async () => await HasScheduledEntryAsync(instanceId, variant.Timer),
            $"'{variant.Timer}' was never armed — {await DescribeAsync(Workflow, instanceId)}",
            TimeSpan.FromSeconds(20));
        Assert.Equal("B", (await GetInstanceStateAsync(Workflow, instanceId)).Status);
        return instanceId;
    }

    /// <summary>Whether the state function lists <paramref name="timer"/> as an armed scheduled entry.</summary>
    private async Task<bool> HasScheduledEntryAsync(string instanceId, string timer)
    {
        var response = await Api.CallInstanceFunctionAsync(Workflow, instanceId, "state", headers: Headers());
        if (!response.Body.TryGetProperty("transitions", out var transitions)) return false;

        foreach (var transition in transitions.EnumerateArray())
        {
            if (transition.TryGetProperty("kind", out var kind) && kind.GetString() == "scheduled"
                && transition.TryGetProperty("name", out var name) && name.GetString() == timer)
                return true;
        }

        return false;
    }
}
