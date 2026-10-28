using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AutoPriorityLab;

/// <summary>
/// Automatic transition priority (vnext#1133). One state (<c>evaluate</c>) declares three automatic
/// transitions in the order <c>to-low</c> (priority 30), <c>to-high</c> (priority 10) and
/// <c>to-fallback</c> (default, <c>triggerKind: 10</c>, no rule, no priority). The runtime must
/// evaluate by ascending priority, not declaration order, and the default transition is always last.
/// <para>
/// Both rules would answer true, so the winner can only be decided by ordering: a runtime that
/// ignores <c>priority</c> ends in <c>low</c> (declared first); one that evaluates the default
/// transition first ends in <c>fallback</c>. Each test therefore also asserts, from the history,
/// that <c>fallback</c> was never reached.
/// </para>
/// </summary>
public class AutoPriorityLabTests : WorkflowTestBase
{
    private const string Workflow = "auto-priority-lab";

    public AutoPriorityLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>Both rules true: priority 10 (<c>high</c>) beats priority 30 declared before it.</summary>
    [Fact]
    public async Task BothRulesTrue_HigherPriorityWinsDespiteDeclarationOrder()
    {
        var instanceId = await StartAsync(Workflow, new { testId = TestId("both") });

        await AssertCompletedInAsync(instanceId, "high");
        await AssertFallbackNotReachedAsync(instanceId, expectedAutoTransition: "to-high");
    }

    /// <summary>The priority-10 rule answers false: the next priority (30, <c>low</c>) takes over.</summary>
    [Fact]
    public async Task HigherPriorityRuleFalse_NextPriorityWins()
    {
        var instanceId = await StartAsync(Workflow, new { skipHigh = true, testId = TestId("skip") });

        await AssertCompletedInAsync(instanceId, "low");
        await AssertFallbackNotReachedAsync(instanceId, expectedAutoTransition: "to-low");
    }

    private async Task AssertCompletedInAsync(string instanceId, string expectedState)
    {
        await WaitForInstanceStateAsync(Workflow, instanceId, expectedState, timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(Workflow, instanceId);

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal(expectedState, state);
        Assert.Equal("C", status);
    }

    /// <summary>
    /// The history holds the auto hop that actually ran and no hop into <c>fallback</c>. The default
    /// transition is always satisfied, so reaching it would mean it was evaluated before the
    /// prioritised ones.
    /// </summary>
    private async Task AssertFallbackNotReachedAsync(string instanceId, string expectedAutoTransition)
    {
        var (status, raw) = await SendRawAsync(
            HttpMethod.Get, $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"history answered {(int)status}: {raw}");

        var rows = JsonDocument.Parse(raw).RootElement.GetProperty("transitions").EnumerateArray().ToList();

        Assert.DoesNotContain(rows, row => row.GetProperty("toState").GetString() == "fallback");
        Assert.DoesNotContain(rows, row => row.GetProperty("transitionId").GetString() == "to-fallback");
        Assert.Contains(rows, row => row.GetProperty("transitionId").GetString() == expectedAutoTransition);
    }

    private static string TestId(string tag) => $"auto-priority-{tag}-{Guid.NewGuid():N}"[..32];
}
