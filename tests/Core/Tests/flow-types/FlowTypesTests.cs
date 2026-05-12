using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;
using Xunit;

namespace Core.IntegrationTests.Tests.FlowTypes;

/// <summary>
/// Smoke tests for all four vNext workflow types (<c>C</c>, <c>P</c>, <c>F</c>, <c>S</c>).
/// Each workflow in <c>core/Workflows/flow-types/</c> follows the same minimal pattern:
/// <c>start → init-state (onEntries mapping writes flowType) → auto transition → completed (final)</c>.
/// The tests verify:
/// <list type="bullet">
///   <item>Instance starts successfully (runtime accepts the flow type).</item>
///   <item>Auto transition fires and the instance reaches the final state with <c>status: "C"</c>.</item>
///   <item>Init mapping writes the expected <c>flowType</c> attribute.</item>
/// </list>
/// </summary>
public class FlowTypesTests : IntegrationTestBase
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    public FlowTypesTests(VNextTestEnvironment environment)
        : base(environment) { }

    [Fact]
    public async Task CoreFlowType_StartsAndCompletesSuccessfully()
    {
        await AssertFlowTypeSmoke("core-flow-test", "core-completed", "C");
    }

    [Fact]
    public async Task SubProcessFlowType_StartsAndCompletesSuccessfully()
    {
        await AssertFlowTypeSmoke("subprocess-flow-test", "subprocess-completed", "P");
    }

    [Fact]
    public async Task FlowFlowType_StartsAndCompletesSuccessfully()
    {
        await AssertFlowTypeSmoke("flow-flow-test", "flow-completed", "F");
    }

    [Fact]
    public async Task SubFlowFlowType_StartsAndCompletesSuccessfully()
    {
        await AssertFlowTypeSmoke("subflow-flow-test", "subflow-completed", "S");
    }

    private async Task AssertFlowTypeSmoke(
        string workflowKey,
        string expectedFinalState,
        string expectedFlowType
    )
    {
        var wf = new WorkflowInstanceTestHelper(Api, workflowKey);
        var body = new
        {
            key = WorkflowInstanceTestHelper.UniqueInstanceKey($"flow-type-{expectedFlowType}"),
            tags = new[] { "integration-test", "flow-types", expectedFlowType },
            attributes = new { },
        };

        var instanceId = await wf.StartInstanceIdAsync(body);
        await wf.WaitForStateAsync(instanceId, expectedFinalState, StateTimeout);

        var stateBody = await wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(stateBody));

        var attrs = await wf.GetAttributesAsync(instanceId);
        JsonElementAssertions.AssertPropertyString(
            attrs,
            "flowType",
            expectedFlowType,
            $"Init mapping should write flowType = \"{expectedFlowType}\"."
        );
    }
}
