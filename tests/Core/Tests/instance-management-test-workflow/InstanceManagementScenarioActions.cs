using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.InstanceManagementTestWorkflow;

/// <summary>
/// Scenario actions tied to <c>instance-management-test-workflow</c>.
/// Encapsulates workflow-specific literals (state names such as <c>active-state</c>,
/// <c>processing-state</c>; the <c>process</c> transition key; start body tag / attribute shape)
/// so the test class stays focused on assertions.
/// <para>
/// Per vnext-tests-as-code layout rules: logic bound to the data contract or state literals of a
/// single workflow lives next to that scenario's <c>*Tests.cs</c> file, not under
/// <c>tests/Core/Helpers</c>. Generic primitives (HTTP orchestration, list JSON parsing, JSON
/// assertions) stay in <see cref="WorkflowInstanceTestHelper"/> / <see cref="InstanceListJson"/>
/// / <see cref="JsonElementAssertions"/>.
/// </para>
/// </summary>
public sealed class InstanceManagementScenarioActions
{
    private readonly WorkflowInstanceTestHelper _wf;
    private readonly TimeSpan _shortTimeout;

    public InstanceManagementScenarioActions(WorkflowInstanceTestHelper wf, TimeSpan shortTimeout)
    {
        _wf = wf;
        _shortTimeout = shortTimeout;
    }

    /// <summary>
    /// Starts an instance with optional <paramref name="category"/> / <paramref name="priority"/>.
    /// When both are null, the start body omits <c>attributes</c> entirely so
    /// <c>InitInstanceMgmtMapping</c> applies defaults (<c>category = "default"</c>, <c>priority = 1</c>).
    /// </summary>
    public Task<string> StartDefaultInstanceAsync(string? category, int? priority) =>
        _wf.StartInstanceIdAsync(BuildStartBody(category, priority, tagSuffix: "default"));

    /// <summary>
    /// Starts an instance and waits until it reaches <c>active-state</c>.
    /// List / filter / sort tests need a well-defined source state per instance.
    /// </summary>
    public async Task<string> StartAndAdvanceToActiveAsync(string? category, int? priority)
    {
        var instanceId = await _wf.StartInstanceIdAsync(
            BuildStartBody(category, priority, tagSuffix: "list-filter")
        );
        await _wf.WaitForStateAsync(instanceId, "active-state", _shortTimeout);
        return instanceId;
    }

    /// <summary>
    /// Runs the <c>active-state → processing-state → &lt;expectedFinalState&gt;</c> chain using the
    /// <c>process</c> transition followed by <paramref name="processingTransition"/> (e.g.
    /// <c>suspend</c>, <c>set-busy</c>, <c>assign-human</c>). Asserts the terminal instance
    /// <c>status</c> is <c>"C"</c>.
    /// </summary>
    public async Task RunThreeStepTransitionAsync(
        string instanceId,
        string processingTransition,
        string expectedFinalState
    )
    {
        await _wf.WaitForStateAsync(instanceId, "active-state", _shortTimeout);
        await _wf.RunTransitionAsync(instanceId, "process", headers: null);
        await _wf.WaitForStateAsync(instanceId, "processing-state", _shortTimeout);
        await _wf.RunTransitionAsync(instanceId, processingTransition, headers: null);
        await _wf.WaitForStateAsync(instanceId, expectedFinalState, _shortTimeout);

        var body = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        // On reaching a final state, status should be "C" (happy-path terminal).
        Assert.Equal("C", StateFunctionJson.ExtractStatus(body));
    }

    /// <summary>
    /// Builds the start body used across this workflow's integration tests. The <c>tags</c> array
    /// carries a scenario-specific suffix so filter / list queries can be scoped when debugging.
    /// </summary>
    public static object BuildStartBody(string? category, int? priority, string tagSuffix)
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey($"instance-mgmt-{tagSuffix}");
        var tags = new[] { "integration-test", "instance-management", tagSuffix };

        if (category is null && priority is null)
            return new { key, tags };

        if (category is null)
            return new
            {
                key,
                tags,
                attributes = new { priority = priority!.Value },
            };

        if (priority is null)
            return new
            {
                key,
                tags,
                attributes = new { category },
            };

        return new
        {
            key,
            tags,
            attributes = new { category, priority = priority.Value },
        };
    }
}
