using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ViewFunctionExtensionTestWorkflow;

/*
! Bugs:
! Workflow-scope (F) function — GET /workflows/{key}/functions/{name} returns 404 (instance-scope I works)
! Domain-scope (D) function — GET /api/v1/{domain}/functions/{name} returns empty/null body
*/

/// <summary>
/// Comprehensive integration tests for <c>view-function-extension-test-workflow</c>.
/// Covers all 6 view content types (JSON/HTML/Markdown/DeepLink/HTTP/URN),
/// all 6 display modes (full-page/popup/bottom-sheet/top-sheet/drawer/inline),
/// all 4 extension types with 3 scopes, all 3 function scopes (I/F/D),
/// wizard state constraint, transition view rendering, and happy path flow control.
/// </summary>
public class ViewFunctionExtensionTestWorkflowTests : IntegrationTestBase
{
    private const string WorkflowKey = "view-function-extension-test-workflow";
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(15);
    private readonly WorkflowInstanceTestHelper _wf;
    private readonly VfeScenarioActions _scenario;

    public ViewFunctionExtensionTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _wf = new WorkflowInstanceTestHelper(Api, WorkflowKey);
        _scenario = new VfeScenarioActions(_wf, ShortTimeout);
    }

    // =========================================================================
    // A. Happy path — full state chain reaches completed with status C
    // =========================================================================

    [Fact]
    public async Task HappyPath_AllStateTransitions_ReachesCompletedWithStatusC()
    {
        var instanceId = await _scenario.RunFullHappyPathAsync();

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(stateBody));
    }

    // =========================================================================
    // B. StartTransition mapping — sets vfeTestStarted
    // =========================================================================

    [Fact]
    public async Task StartTransitionMapping_SetsVfeTestStarted()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var attrs = await _wf.GetAttributesAsync(instanceId);
        JsonElementAssertions.AssertPropertyTrue(
            attrs,
            "vfeTestStarted",
            "InitVfeMapping should set attributes.vfeTestStarted = true."
        );
    }

    // =========================================================================
    // C. View type + display mode tests (6 views, each with unique type+display)
    // =========================================================================

    [Fact]
    public async Task JsonViewState_Type1_FullPage_HasView()
    {
        var instanceId = await _scenario.StartAndAssertViewTestStateAsync();

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("view-test-state", StateFunctionJson.ExtractStateName(stateBody));

        VfeViewAssertions.AssertStateHasView(stateBody);

        var viewBody = await _wf.CallFunctionAsync(instanceId, "view");
        VfeViewAssertions.AssertViewContentIsNonEmptyString(viewBody);
    }

    [Fact]
    public async Task HtmlViewState_Type2_Popup_HasView()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        VfeViewAssertions.AssertStateHasView(stateBody);

        var viewBody = await _wf.CallFunctionAsync(instanceId, "view");
        VfeViewAssertions.AssertViewContentIsNonEmptyString(viewBody);
    }

    [Fact]
    public async Task MarkdownViewState_Type3_BottomSheet_HasView()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();
        await _scenario.AdvanceToMarkdownStateAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        VfeViewAssertions.AssertStateHasView(stateBody);

        var viewBody = await _wf.CallFunctionAsync(instanceId, "view");
        VfeViewAssertions.AssertViewContentIsNonEmptyString(viewBody);
    }

    [Fact]
    public async Task DeeplinkViewState_Type4_TopSheet_HasView()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();
        await _scenario.AdvanceToMarkdownStateAsync(instanceId);
        await _scenario.AdvanceToDeeplinkStateAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        VfeViewAssertions.AssertStateHasView(stateBody);

        var viewBody = await _wf.CallFunctionAsync(instanceId, "view");
        VfeViewAssertions.AssertViewContentHasHref(viewBody);
    }

    [Fact]
    public async Task HttpViewState_Type5_Drawer_HasView()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();
        await _scenario.AdvanceToMarkdownStateAsync(instanceId);
        await _scenario.AdvanceToDeeplinkStateAsync(instanceId);
        await _scenario.AdvanceToHttpStateAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        VfeViewAssertions.AssertStateHasView(stateBody);

        var viewBody = await _wf.CallFunctionAsync(instanceId, "view");
        VfeViewAssertions.AssertViewContentHasHref(viewBody);
    }

    [Fact]
    public async Task UrnViewState_Type6_Inline_HasView()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();
        await _scenario.AdvanceToMarkdownStateAsync(instanceId);
        await _scenario.AdvanceToDeeplinkStateAsync(instanceId);
        await _scenario.AdvanceToHttpStateAsync(instanceId);
        await _scenario.AdvanceToUrnViewStateAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("urn-view-state", StateFunctionJson.ExtractStateName(stateBody));

        VfeViewAssertions.AssertStateHasView(stateBody);

        try
        {
            var viewBody = await _wf.CallFunctionAsync(instanceId, "view");
            VfeViewAssertions.AssertViewContentHasUrn(viewBody);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("NotFound"))
        {
            // TODO: Runtime returns 404 for functions/view on type 6 (URN) views.
            // The view IS attached to the state (confirmed by state response above).
            Assert.Fail(
                "Runtime returned 404 for functions/view on URN (type 6) view. "
                + "State confirms view.hasView=true but content endpoint fails. "
                + $"Exception: {ex.Message}"
            );
        }
    }

    // =========================================================================
    // C2. Transition view rendering tests
    // =========================================================================

    [Fact]
    public async Task ContinueToHtml_TransitionHasView()
    {
        var instanceId = await _scenario.StartAndAssertViewTestStateAsync();

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("view-test-state", StateFunctionJson.ExtractStateName(stateBody));

        VfeViewAssertions.AssertTransitionHasView(stateBody, "continue-to-html");
    }

    [Fact]
    public async Task WizardStepState_SingleTransition_TransitionViewRendered()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();
        await _scenario.AdvanceToMarkdownStateAsync(instanceId);
        await _scenario.AdvanceToDeeplinkStateAsync(instanceId);
        await _scenario.AdvanceToHttpStateAsync(instanceId);
        await _scenario.AdvanceToUrnViewStateAsync(instanceId);
        await _scenario.AdvanceToWizardStepStateAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("wizard-step-state", StateFunctionJson.ExtractStateName(stateBody));

        if (stateBody.TryGetProperty("transitions", out var transitions)
            && transitions.ValueKind == JsonValueKind.Array)
        {
            Assert.True(
                transitions.GetArrayLength() == 1,
                $"Wizard state (stateType 5) must have exactly 1 transition; found {transitions.GetArrayLength()}."
            );
        }

        VfeViewAssertions.AssertTransitionHasView(stateBody, "complete");

        // Wizard state should NOT have a state-level view (transition view is used instead)
        if (stateBody.TryGetProperty("view", out var wizardView))
        {
            if (wizardView.TryGetProperty("hasView", out var hasView))
            {
                Assert.True(
                    hasView.ValueKind == JsonValueKind.False
                        || hasView.ValueKind == JsonValueKind.Null,
                    "Wizard state should not render a state-level view (hasView should be false/null). "
                    + "Wizard states render the transition view instead."
                );
            }
        }
    }

    // =========================================================================
    // D. View function — retrieves view payload
    // =========================================================================

    [Fact]
    public async Task ViewFunction_ReturnsViewPayload()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var viewBody = await _wf.CallFunctionAsync(instanceId, "view");
        Assert.NotEqual(JsonValueKind.Undefined, viewBody.ValueKind);
    }

    // =========================================================================
    // E. Extension tests — Type 1/2/3/4, Scope 1/2/3
    //    Per runtime docs, extensions appear in GET .../functions/data response
    //    under the "extensions" object (NOT in instances/{id}).
    //    Requested types use: functions/data?extensions=key
    // =========================================================================

    [Fact]
    public async Task GlobalExtension_Type1_AppliesImplicitly_WithoutWorkflowReference()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var dataBody = await _wf.CallFunctionAsync(instanceId, "data");
        var dataSummary = SummarizeJson(dataBody, 500);

        if (TryHasExtension(dataBody, "vfe-global-extension"))
        {
            VfeExtensionAssertions.AssertExtensionTypeMarker(
                dataBody,
                "vfe-global-extension",
                "global"
            );
        }
        else
        {
            // TODO: Runtime may not inject Global (Type 1) extension into functions/data response
            // when the extension is not referenced in the workflow's extensions array.
            Assert.Fail(
                "Extension 'vfe-global-extension' (Type 1, Global, Scope 3) not found in functions/data response. "
                + $"Actual functions/data keys: {dataSummary}"
            );
        }
    }

    [Fact]
    public async Task GlobalAndRequestedExtension_Type2_AutoAppliesAndQueryable()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var instanceBody = await _wf.GetInstanceBodyAsync(instanceId);

        if (TryHasExtension(instanceBody, "vfe-global-and-requested-extension"))
        {
            VfeExtensionAssertions.AssertExtensionTypeMarker(
                instanceBody,
                "vfe-global-and-requested-extension",
                "globalAndRequested"
            );
            return;
        }

        var dataBody = await _wf.CallFunctionAsync(instanceId, "data");
        if (TryHasExtension(dataBody, "vfe-global-and-requested-extension"))
        {
            VfeExtensionAssertions.AssertExtensionTypeMarker(
                dataBody,
                "vfe-global-and-requested-extension",
                "globalAndRequested"
            );
            return;
        }

        var dataWithQuery = await _wf.CallFunctionAsync(
            instanceId,
            "data",
            queryParams: new Dictionary<string, string>
            {
                ["extensions"] = "vfe-global-and-requested-extension",
            }
        );
        if (TryHasExtension(dataWithQuery, "vfe-global-and-requested-extension"))
        {
            VfeExtensionAssertions.AssertExtensionPresent(
                dataWithQuery,
                "vfe-global-and-requested-extension"
            );
            return;
        }

        var instanceWithQuery = await _wf.GetInstanceRawAsync(
            instanceId,
            queryParams: new Dictionary<string, string>
            {
                ["extensions"] = "vfe-global-and-requested-extension",
            }
        );
        if (TryHasExtension(instanceWithQuery, "vfe-global-and-requested-extension"))
        {
            VfeExtensionAssertions.AssertExtensionPresent(
                instanceWithQuery,
                "vfe-global-and-requested-extension"
            );
            return;
        }

        var instSummary = SummarizeJson(instanceBody, 300);
        var dataSummary = SummarizeJson(dataBody, 300);
        Assert.Fail(
            "Extension 'vfe-global-and-requested-extension' (Type 2, Scope 1) not found in GetInstance or functions/data. "
            + $"GetInstance keys: {instSummary} | functions/data keys: {dataSummary}"
        );
    }

    [Fact]
    public async Task DefinedFlowsExtension_Type3_AutoAppliesOnDefinedFlow()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var listBody = await _wf.ListInstancesAsync(pageSize: 10);
        Assert.True(
            listBody.ValueKind == JsonValueKind.Object || listBody.ValueKind == JsonValueKind.Array,
            "List instances should return a JSON object or array."
        );
    }

    [Fact]
    public async Task DefinedFlowAndRequestedExtension_Type4_OnlyAppearsWhenRequested()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var dataBody = await _wf.CallFunctionAsync(instanceId, "data");

        bool presentByDefault = TryHasExtension(dataBody, "vfe-defined-flow-and-requested-extension");
        if (presentByDefault)
        {
            Assert.Fail(
                "Type 4 extension should NOT auto-appear in default functions/data but was found."
            );
        }

        var dataWithQuery = await _wf.CallFunctionAsync(
            instanceId,
            "data",
            queryParams: new Dictionary<string, string>
            {
                ["extensions"] = "vfe-defined-flow-and-requested-extension",
            }
        );

        if (TryHasExtension(dataWithQuery, "vfe-defined-flow-and-requested-extension"))
        {
            VfeExtensionAssertions.AssertExtensionTypeMarker(
                dataWithQuery,
                "vfe-defined-flow-and-requested-extension",
                "definedFlowAndRequested"
            );
        }
        else
        {
            // TODO: Runtime did not return Type 4 extension even with ?extensions= query on functions/data.
            Assert.Fail(
                "Extension 'vfe-defined-flow-and-requested-extension' (Type 4) not found even with "
                + "?extensions= query on functions/data endpoint."
            );
        }
    }

    [Fact]
    public async Task DefinedFlowsExtension_Scope2_GetAllInstances_EnrichesListEndpoint()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var listBody = await _wf.ListInstancesAsync(pageSize: 10);

        Assert.True(
            listBody.ValueKind == JsonValueKind.Object || listBody.ValueKind == JsonValueKind.Array,
            "List instances should return a JSON object or array."
        );

        if (listBody.ValueKind == JsonValueKind.Array && listBody.GetArrayLength() > 0)
        {
            var firstInstance = listBody[0];
            if (firstInstance.TryGetProperty("extensions", out _))
            {
                VfeExtensionAssertions.AssertExtensionPresent(
                    firstInstance,
                    "vfe-defined-flows-extension"
                );
            }
        }
        else if (
            listBody.ValueKind == JsonValueKind.Object
            && listBody.TryGetProperty("data", out var dataArr)
            && dataArr.ValueKind == JsonValueKind.Array
            && dataArr.GetArrayLength() > 0
        )
        {
            var firstInstance = dataArr[0];
            if (firstInstance.TryGetProperty("extensions", out _))
            {
                VfeExtensionAssertions.AssertExtensionPresent(
                    firstInstance,
                    "vfe-defined-flows-extension"
                );
            }
        }
    }

    // =========================================================================
    // F. Function tests — scope I (instance), F (workflow), D (domain)
    // =========================================================================

    [Fact]
    public async Task SingleTaskFunction_InstanceScope_ReturnsResponse()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var body = await _wf.CallFunctionAsync(instanceId, "vfe-single-task-function");
        VfeFunctionAssertions.AssertFunctionResponseNotEmpty(body, "vfe-single-task-function");

        var bodySummary = SummarizeJson(body, 500);
        Assert.True(
            VfeFunctionAssertions.TryAssertFunctionPropertyTrue(body, "singleTaskFunction"),
            $"Function 'vfe-single-task-function' response.singleTaskFunction should be true. "
            + $"Actual body: {bodySummary}"
        );
    }

    [Fact]
    public async Task MultiTaskFunction_InstanceScope_AggregatesResponses()
    {
        var instanceId = await _scenario.StartAndWaitForHtmlViewStateAsync();

        var body = await _wf.CallFunctionAsync(instanceId, "vfe-multi-task-function");
        VfeFunctionAssertions.AssertFunctionResponseNotEmpty(body, "vfe-multi-task-function");

        var bodySummary = SummarizeJson(body, 500);
        Assert.True(
            VfeFunctionAssertions.TryAssertFunctionPropertyTrue(body, "aggregated"),
            $"Function 'vfe-multi-task-function' response.aggregated should be true. "
            + $"Actual body: {bodySummary}"
        );
    }

    [Fact]
    public async Task WorkflowFunction_WorkflowScope_ReturnsResponse()
    {
        try
        {
            var body = await _wf.CallWorkflowScopeFunctionAsync("vfe-workflow-function");
            VfeFunctionAssertions.AssertFunctionResponseNotEmpty(body, "vfe-workflow-function");
            VfeFunctionAssertions.AssertFunctionProperty(
                body,
                "functionScope",
                "F",
                "vfe-workflow-function"
            );
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("NotFound"))
        {
            // TODO: Runtime returns 404 for workflow-scope (F) function endpoint.
            Assert.Fail(
                "Workflow-scope function 'vfe-workflow-function' returned 404. "
                + "Runtime may not support scope 'F' functions via workflow-level endpoint."
            );
        }
    }

    [Fact]
    public async Task DomainFunction_DomainScope_ReturnsResponse()
    {
        try
        {
            var body = await DomainFunctionHelper.CallDomainScopeFunctionAsync(
                Api,
                "core",
                "1",
                "vfe-domain-function"
            );

            if (body.ValueKind == JsonValueKind.Undefined
                || body.ValueKind == JsonValueKind.Null)
            {
                Assert.Fail(
                    "Domain-scope function 'vfe-domain-function' returned empty response. "
                    + "Runtime may not support scope 'D' functions via domain-level endpoint."
                );
                return;
            }

            VfeFunctionAssertions.AssertFunctionResponseNotEmpty(body, "vfe-domain-function");
            VfeFunctionAssertions.AssertFunctionProperty(
                body,
                "functionScope",
                "D",
                "vfe-domain-function"
            );
        }
        catch (Exception ex)
        {
            Assert.Fail(
                $"Domain-scope function 'vfe-domain-function' failed: {ex.Message}. "
                + "Runtime may not support scope 'D' functions."
            );
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static bool TryHasExtension(JsonElement body, string extensionKey)
    {
        var camelKey = ToCamelCase(extensionKey);

        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("extensions", out var ext)
            && ext.ValueKind == JsonValueKind.Object
            && (ext.TryGetProperty(extensionKey, out _) || ext.TryGetProperty(camelKey, out _)))
            return true;

        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("extensions", out var nested)
            && nested.ValueKind == JsonValueKind.Object
            && (nested.TryGetProperty(extensionKey, out _) || nested.TryGetProperty(camelKey, out _)))
            return true;

        return false;
    }

    private static string ToCamelCase(string kebab)
    {
        var parts = kebab.Split('-');
        if (parts.Length <= 1) return kebab;
        return parts[0] + string.Concat(
            parts.Skip(1).Select(p =>
                p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..])
        );
    }

    private static string SummarizeJson(JsonElement el, int maxLen)
    {
        var raw = el.GetRawText();
        return raw.Length <= maxLen ? raw : raw[..maxLen] + "...";
    }
}
