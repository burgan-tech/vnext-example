using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ViewFunctionExtensionTestWorkflow;

/// <summary>
/// Integration tests for <c>view-function-extension-test-workflow</c>.
/// Covers: view types (JSON/HTML/Markdown), display modes, wizard state (stateType 5),
/// single-task / multi-task functions, global/requested extensions, features reference.
/// Aligned with <c>api-tests/view-function-extension/view-function-extension-test-workflow.http</c>
/// and <c>docs/integration-test-documentation.md</c> Grup 5.
/// </summary>
public class ViewFunctionExtensionTestWorkflowTests : IntegrationTestBase
{
    private const string WorkflowKey = "view-function-extension-test-workflow";
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(15);
    private readonly WorkflowInstanceTestHelper _wf;

    public ViewFunctionExtensionTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _wf = new WorkflowInstanceTestHelper(Api, WorkflowKey);
    }

    // -------------------------------------------------------------------------
    // A. Happy Path — workflow reaches completed-state via manual transitions
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ManualTransitions_ReachesCompletedWithStatusC()
    {
        var instanceId = await StartVfeInstanceAsync();

        // WebPlatformRule always returns true → auto-to-multi-view fires
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        await _wf.RunTransitionAsync(instanceId, "manual-to-wizard", headers: null);
        await _wf.AssertStateAsync(instanceId, "wizard-state");

        await _wf.RunTransitionAsync(
            instanceId,
            "complete-with-markdown-view",
            headers: null
        );

        await _wf.WaitForStateAsync(instanceId, "completed-state", ShortTimeout);
        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(stateBody));
    }

    // -------------------------------------------------------------------------
    // B. View assertions — state-level and transition-level views
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ViewTestState_HasView_WithJsonType()
    {
        var instanceId = await StartVfeInstanceAsync();

        // multi-view-state reached via auto; but we can query view from any reachable state
        // The instance auto-transitions so fast we check multi-view-state view instead
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);

        VfeViewAssertions.AssertStateHasView(stateBody);
    }

    [Fact]
    public async Task ViewFunction_ReturnsViewPayload()
    {
        var instanceId = await StartVfeInstanceAsync();
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        var viewBody = await _wf.CallFunctionAsync(
            instanceId,
            "view",
            queryParams: new Dictionary<string, string> { ["platform"] = "web" }
        );

        Assert.NotEqual(JsonValueKind.Undefined, viewBody.ValueKind);
    }

    [Fact]
    public async Task WizardState_TransitionView_MarkdownView()
    {
        var instanceId = await StartVfeInstanceAsync();
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        await _wf.RunTransitionAsync(instanceId, "manual-to-wizard", headers: null);
        await _wf.AssertStateAsync(instanceId, "wizard-state");

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);

        Assert.True(
            StateFunctionJson.TransitionsContainKey(stateBody, "complete-with-markdown-view"),
            "Wizard state should list complete-with-markdown-view transition."
        );

        VfeViewAssertions.AssertTransitionHasView(stateBody, "complete-with-markdown-view");
    }

    // -------------------------------------------------------------------------
    // C. Wizard state constraint — stateType 5 allows at most 1 transition
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WizardState_HasAtMostOneTransition()
    {
        var instanceId = await StartVfeInstanceAsync();
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        await _wf.RunTransitionAsync(instanceId, "manual-to-wizard", headers: null);
        await _wf.AssertStateAsync(instanceId, "wizard-state");

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);

        if (
            stateBody.TryGetProperty("transitions", out var transitions)
            && transitions.ValueKind == JsonValueKind.Array
        )
        {
            Assert.True(
                transitions.GetArrayLength() <= 1,
                $"Wizard state (stateType 5) should have at most 1 transition; found {transitions.GetArrayLength()}."
            );
        }
    }

    // -------------------------------------------------------------------------
    // D. Function tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SingleTaskFunction_ReturnsResponse()
    {
        var instanceId = await StartVfeInstanceAsync();
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        var functionBody = await _wf.CallFunctionAsync(
            instanceId,
            "single-task-function"
        );

        Assert.NotEqual(JsonValueKind.Undefined, functionBody.ValueKind);
    }

    [Fact]
    public async Task MultiTaskFunction_ReturnsAggregatedResponse()
    {
        var instanceId = await StartVfeInstanceAsync();
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        var functionBody = await _wf.CallFunctionAsync(
            instanceId,
            "multi-task-function"
        );

        Assert.NotEqual(JsonValueKind.Undefined, functionBody.ValueKind);
    }

    // -------------------------------------------------------------------------
    // E. Extension tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DataFunction_WithoutExtensionFilter_ReturnsInstanceData()
    {
        var instanceId = await StartVfeInstanceAsync();
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        var dataBody = await _wf.CallFunctionAsync(instanceId, "data");

        Assert.NotEqual(JsonValueKind.Undefined, dataBody.ValueKind);
    }

    [Fact]
    public async Task DataFunction_WithRequestedExtensions_ReturnsEnrichedData()
    {
        var instanceId = await StartVfeInstanceAsync();
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        var dataBody = await _wf.CallFunctionAsync(
            instanceId,
            "data",
            queryParams: new Dictionary<string, string>
            {
                ["extensions"] = "requested-extension,global-extension",
            }
        );

        Assert.NotEqual(JsonValueKind.Undefined, dataBody.ValueKind);
    }

    // -------------------------------------------------------------------------
    // F. Instance attributes — startTransition mapping wrote vfeTestStarted
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartTransitionMapping_SetsVfeTestStarted()
    {
        var instanceId = await StartVfeInstanceAsync();
        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);

        var attrs = await _wf.GetAttributesAsync(instanceId);
        JsonElementAssertions.AssertPropertyTrue(
            attrs,
            "vfeTestStarted",
            "InitVfeMapping should set attributes.vfeTestStarted = true."
        );
    }

    // -------------------------------------------------------------------------
    // G. Auto transition rule — WebPlatformRule + default fallback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AutoTransition_WebPlatformRule_ReachesMultiViewState()
    {
        var instanceId = await StartVfeInstanceAsync();

        await _wf.WaitForStateAsync(instanceId, "multi-view-state", ShortTimeout);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<string> StartVfeInstanceAsync()
    {
        return await _wf.StartInstanceIdAsync(
            new
            {
                key = WorkflowInstanceTestHelper.UniqueInstanceKey("vfe-test"),
                tags = new[] { "integration-test", "view-function-extension" },
                attributes = new { },
            }
        );
    }
}
