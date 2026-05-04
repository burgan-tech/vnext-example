using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;
using Xunit;

namespace Core.IntegrationTests.Tests.InstanceManagementTestWorkflow;

/// <summary>
/// Integration tests for <c>core/Workflows/instance-management/instance-management-test-workflow.json</c>.
/// Coverage aligns with the <c>doc/integration-test-documentation.md</c> "Group 7: Instance Management" matrix
/// and steps in <c>api-tests/instance-management/postman-instance-management.json</c>.
///
/// Features under test:
/// <list type="bullet">
///   <item>Manual transition variants (process/fast-complete/finish/reject/suspend/set-busy/assign-human)</item>
///   <item>Final state subType 1 / 2 / 4 / 5 / 6 (completed / rejected / suspended / busy / human)</item>
///   <item>Workflow timeout (PT120S → timeout-state subType 3)</item>
///   <item>Idempotent start (two POSTs with the same key return the same instance id)</item>
///   <item>Instance list filtering — GraphQL / JSON shape (attributes, currentState, status)</item>
///   <item>Pagination (page / pageSize); list sort by <c>createdAt</c> asc/desc without filter where sort is isolated</item>
///   <item><c>InitInstanceMgmtMapping</c> contract (category / priority / testStarted / startedAt)</item>
/// </list>
///
/// Notes:
/// - The <c>filter</c> parameter is always in <b>GraphQL / JSON</b> form (vnext-workflow-creation §7).
/// - The SDK applies HttpClient URL encoding via <c>ListInstancesAsync</c> query-parameter dictionary;
///   when the filter JSON string is passed as a dictionary value, <c>{</c>/<c>}</c>/<c>"</c> are encoded automatically.
/// </summary>
public class InstanceManagementTestWorkflowTests : IntegrationTestBase
{
    private const string WorkflowKey = "instance-management-test-workflow";

    /// <summary>Tolerance window for short manual-transition flows.</summary>
    private static readonly TimeSpan ShortStateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Workflow timeout is <c>PT120S</c> (2 minutes). Conservative upper bound ~3 minutes for slow CI.
    /// (No lower bound; PollStateUntilAnyAsync does not short-circuit the “did the timer actually wait?” case.)
    /// </summary>
    private static readonly TimeSpan WorkflowTimeoutWait = TimeSpan.FromMinutes(3);

    private readonly WorkflowInstanceTestHelper _wf;

    public InstanceManagementTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _wf = new WorkflowInstanceTestHelper(Api, WorkflowKey);
    }

    // -----------------------------------------------------------------------
    //  Transition variants + final state subTypes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ProcessThenFinish_ReachesCompletedState()
    {
        var instanceId = await StartDefaultInstanceAsync("finance", 1);
        await _wf.WaitForStateAsync(instanceId, "active-state", ShortStateTimeout);

        await _wf.RunTransitionAsync(instanceId, "process", headers: null);
        await _wf.WaitForStateAsync(instanceId, "processing-state", ShortStateTimeout);

        await _wf.RunTransitionAsync(instanceId, "finish", headers: null);
        await _wf.WaitForStateAsync(instanceId, "completed-state", ShortStateTimeout);

        // completed-state subType:1 → happy-path final: status "C" (Completed).
        var body = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(body));

        var attrs = await GetAttributesAsync(WorkflowKey, instanceId);
        InstanceManagementInstanceDataAssertions.AssertInitialAttributes(
            attrs,
            expectedCategory: "finance",
            expectedPriority: 1
        );
    }

    [Fact]
    public async Task FastComplete_FromActive_ReachesCompletedStateWithoutProcessing()
    {
        var instanceId = await StartDefaultInstanceAsync(category: null, priority: null);

        await _wf.WaitForStateAsync(instanceId, "active-state", ShortStateTimeout);
        await _wf.RunTransitionAsync(instanceId, "fast-complete", headers: null);
        await _wf.WaitForStateAsync(instanceId, "completed-state", ShortStateTimeout);

        var body = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(body));

        // When category/priority are omitted from the start body, init mapping applies defaults.
        var attrs = await GetAttributesAsync(WorkflowKey, instanceId);
        InstanceManagementInstanceDataAssertions.AssertInitialAttributes(
            attrs,
            expectedCategory: "default",
            expectedPriority: 1
        );
    }

    [Fact]
    public async Task RejectPath_ReachesRejectedState_SubType2_StillCompleted()
    {
        var instanceId = await StartDefaultInstanceAsync(category: "risky", priority: 3);

        await _wf.WaitForStateAsync(instanceId, "active-state", ShortStateTimeout);
        await _wf.RunTransitionAsync(instanceId, "process", headers: null);
        await _wf.WaitForStateAsync(instanceId, "processing-state", ShortStateTimeout);
        await _wf.RunTransitionAsync(instanceId, "reject", headers: null);
        await _wf.WaitForStateAsync(instanceId, "rejected-state", ShortStateTimeout);

        // rejected-state is stateType:3 (final) + subType:2; instance reached a final state so status is "C".
        // Note: status is NOT "F" — "F" means faulted/exception; here reject is a normal terminal state, so runtime marks Completed (C).
        var body = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(body));
    }

    [Fact]
    public async Task SuspendPath_ReachesSuspendedState_SubType4()
    {
        var instanceId = await StartDefaultInstanceAsync(category: "ops", priority: 2);
        await RunThreeStepTransitionAsync(instanceId, "suspend", "suspended-state");

        // subType:4 (Temporarily Suspended) — runtime state subType filtering treats this final state as Suspended (see instance-filtering.md "Instance Subtype (4,5,6) Filtering").
    }

    [Fact]
    public async Task BusyPath_ReachesBusyState_SubType5()
    {
        var instanceId = await StartDefaultInstanceAsync(category: "ops", priority: 2);
        await RunThreeStepTransitionAsync(instanceId, "set-busy", "busy-state");
    }

    [Fact]
    public async Task HumanPath_ReachesHumanState_SubType6()
    {
        var instanceId = await StartDefaultInstanceAsync(category: "ops", priority: 2);
        await RunThreeStepTransitionAsync(instanceId, "assign-human", "human-state");
    }

    // -----------------------------------------------------------------------
    //  Idempotent start (repeat POST with same key → same instance id)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IdempotentStart_SameKey_ReturnsSameInstanceId()
    {
        var uniqueKey = WorkflowInstanceTestHelper.UniqueInstanceKey("idempotent");
        var body = new
        {
            key = uniqueKey,
            tags = new[] { "integration-test", "instance-management", "idempotent" },
            attributes = new { category = "idempotent-test", priority = 1 },
        };

        var firstId = await _wf.StartInstanceIdAsync(body);
        var secondId = await _wf.StartInstanceIdAsync(body);

        Assert.Equal(firstId, secondId);
    }

    // -----------------------------------------------------------------------
    //  Filtering (GraphQL / JSON) — vnext-workflow-creation §7
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Filter_ByCategoryFinance_ListContainsOnlyFinanceInstances()
    {
        var financeId = await StartAndAdvanceToActiveAsync(category: "finance", priority: 1);
        var opsId = await StartAndAdvanceToActiveAsync(category: "ops", priority: 2);

        // filter={"attributes":{"category":{"eq":"finance"}}}
        var body = await ListInstancesWithFilterAsync(
            "{\"attributes\":{\"category\":{\"eq\":\"finance\"}}}"
        );

        Assert.True(
            InstanceListJson.ContainsInstanceId(body, financeId),
            $"finance instance ({financeId}) should be present in category=finance filter result."
        );
        Assert.False(
            InstanceListJson.ContainsInstanceId(body, opsId),
            $"ops instance ({opsId}) should NOT be present in category=finance filter result."
        );

        // Verify every listed instance has attributes.category == "finance".
        foreach (var item in InstanceListJson.ExtractItems(body))
        {
            var attrs = InstanceListJson.TryGetAttributes(item);
            if (attrs is null)
                continue;
            if (attrs.Value.TryGetProperty("category", out var catEl))
            {
                Assert.Equal("finance", catEl.GetString());
            }
        }
    }

    [Fact]
    public async Task Filter_ByCurrentState_ActiveState_ReturnsInstancesInActiveState()
    {
        var activeId = await StartAndAdvanceToActiveAsync(category: "finance", priority: 1);
        var completedId = await StartAndAdvanceToActiveAsync(category: "ops", priority: 1);
        await _wf.RunTransitionAsync(completedId, "fast-complete", headers: null);
        await _wf.WaitForStateAsync(completedId, "completed-state", ShortStateTimeout);

        // filter={"currentState":{"eq":"active-state"}}
        var body = await ListInstancesWithFilterAsync(
            "{\"currentState\":{\"eq\":\"active-state\"}}"
        );

        Assert.True(
            InstanceListJson.ContainsInstanceId(body, activeId),
            $"active instance ({activeId}) should be listed by currentState=active-state filter."
        );
        Assert.False(
            InstanceListJson.ContainsInstanceId(body, completedId),
            $"completed instance ({completedId}) should NOT be listed by currentState=active-state filter."
        );
    }

    [Fact]
    public async Task Filter_ByStatus_ActiveCode_ReturnsOnlyActiveInstances()
    {
        var activeId = await StartAndAdvanceToActiveAsync(category: "finance", priority: 1);
        var completedId = await StartAndAdvanceToActiveAsync(category: "ops", priority: 1);
        await _wf.RunTransitionAsync(completedId, "fast-complete", headers: null);
        await _wf.WaitForStateAsync(completedId, "completed-state", ShortStateTimeout);

        // filter={"status":{"eq":"A"}} — status code "A" (Active).
        var body = await ListInstancesWithFilterAsync("{\"status\":{\"eq\":\"A\"}}");

        Assert.True(
            InstanceListJson.ContainsInstanceId(body, activeId),
            $"active instance ({activeId}) should be listed by status=A filter."
        );
        Assert.False(
            InstanceListJson.ContainsInstanceId(body, completedId),
            $"completed instance ({completedId}) should NOT appear under status=A filter."
        );
    }

    // -----------------------------------------------------------------------
    //  Sorting by createdAt (asc/desc) and pagination
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sort_DescendingByCreatedAt_NewestInstanceAppearsBeforeOldest()
    {
        var firstId = await StartAndAdvanceToActiveAsync(category: "finance", priority: 1);
        // Short delay so createdAt ordering is distinguishable (precision may be sub-second).
        await Task.Delay(1200);
        var secondId = await StartAndAdvanceToActiveAsync(category: "finance", priority: 1);

        // Sort only (no filter) — isolates list sort behaviour. Large pageSize so both instances
        // are likely on the first page in typical integration environments.
        var queryParams = new Dictionary<string, string>
        {
            ["sort"] = "-createdAt",
            ["pageSize"] = "100",
        };
        var response = await Api.ListInstancesAsync(WorkflowKey, queryParams);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var firstIdx = IndexOfInstance(response.Body, firstId);
        var secondIdx = IndexOfInstance(response.Body, secondId);

        Assert.True(firstIdx >= 0, $"first instance ({firstId}) should be listed.");
        Assert.True(secondIdx >= 0, $"second instance ({secondId}) should be listed.");
        Assert.True(
            secondIdx < firstIdx,
            "Newest instance (second) should appear before the older one under sort=-createdAt; "
                + $"secondIdx={secondIdx}, firstIdx={firstIdx}."
        );
    }

    [Fact]
    public async Task Sort_AscendingByCreatedAt_OldestInstanceAppearsBeforeNewest()
    {
        var firstId = await StartAndAdvanceToActiveAsync(category: "finance", priority: 1);
        await Task.Delay(1200);
        var secondId = await StartAndAdvanceToActiveAsync(category: "finance", priority: 1);

        var queryParams = new Dictionary<string, string>
        {
            // Ascending creation time — same shorthand family as `-createdAt` / GetInstances doc (`FieldName`).
            ["sort"] = "createdAt",
            ["pageSize"] = "100",
        };
        var response = await Api.ListInstancesAsync(WorkflowKey, queryParams);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var firstIdx = IndexOfInstance(response.Body, firstId);
        var secondIdx = IndexOfInstance(response.Body, secondId);

        Assert.True(firstIdx >= 0, $"first instance ({firstId}) should be listed.");
        Assert.True(secondIdx >= 0, $"second instance ({secondId}) should be listed.");
        Assert.True(
            firstIdx < secondIdx,
            "Older instance (first) should appear before the newer one under ascending createdAt sort; "
                + $"firstIdx={firstIdx}, secondIdx={secondIdx}."
        );
    }

    [Fact]
    public async Task Pagination_WithPageSize_LimitsPageItemsAndReportsTotalCount()
    {
        // Create at least three distinct finance instances.
        var ids = new List<string>
        {
            await StartAndAdvanceToActiveAsync(category: "finance", priority: 1),
            await StartAndAdvanceToActiveAsync(category: "finance", priority: 1),
            await StartAndAdvanceToActiveAsync(category: "finance", priority: 1),
        };

        var queryParams = new Dictionary<string, string>
        {
            ["filter"] = "{\"attributes\":{\"category\":{\"eq\":\"finance\"}}}",
            ["page"] = "1",
            ["pageSize"] = "2",
            ["sort"] = "-createdAt",
        };
        var response = await Api.ListInstancesAsync(WorkflowKey, queryParams);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = InstanceListJson.ExtractItems(response.Body);
        Assert.True(
            items.Count <= 2,
            $"page=1&pageSize=2 should cap returned items at 2; got {items.Count}."
        );

        // If a pagination object exists, page/pageSize should be reported correctly.
        var page = InstanceListJson.TryGetPaginationInt(response.Body, "page");
        var pageSize = InstanceListJson.TryGetPaginationInt(response.Body, "pageSize");
        var totalCount = InstanceListJson.TryGetPaginationInt(response.Body, "totalCount");
        if (page is not null)
            Assert.Equal(1, page);
        if (pageSize is not null)
            Assert.Equal(2, pageSize);
        if (totalCount is not null)
            Assert.True(
                totalCount >= ids.Count,
                $"pagination.totalCount ({totalCount}) should be >= created finance count ({ids.Count})."
            );
    }

    // -----------------------------------------------------------------------
    //  Workflow timeout (PT120S → timeout-state, subType 3)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Workflow-level timeout moves instances still in active-state after 120 seconds automatically to
    /// <c>timeout-state</c> (stateType:3, subType:3). The test polls with a short period; upper bound ~3 minutes for CI.
    /// </summary>
    [Fact]
    public async Task WorkflowTimeout_AfterPT120S_InstanceAutoMovesToTimeoutState()
    {
        var instanceId = await StartAndAdvanceToActiveAsync(category: "timeout-test", priority: 1);

        // If we leave before 120s the assertion is meaningless; tolerate up to ~3 min poll.
        await _wf.WaitForStateAsync(instanceId, "timeout-state", WorkflowTimeoutWait);

        var body = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        // timeout-state is final so status "C" (Completed) is expected
        // (F = Faulted; no exception here — runtime treats timeout as normal terminal).
        Assert.Equal("C", StateFunctionJson.ExtractStatus(body));
    }

    // -----------------------------------------------------------------------
    //  Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>category</c> and <c>priority</c> are optional; when null the start body omits those fields
    /// and init mapping writes defaults ("default" / 1).
    /// </summary>
    private Task<string> StartDefaultInstanceAsync(string? category, int? priority) =>
        _wf.StartInstanceIdAsync(
            BuildStartBody(category, priority, tagSuffix: "default")
        );

    /// <summary>
    /// Start an instance and wait until it reaches <c>active-state</c>.
    /// List/filter tests need a well-defined source state per instance.
    /// </summary>
    private async Task<string> StartAndAdvanceToActiveAsync(string? category, int? priority)
    {
        var instanceId = await _wf.StartInstanceIdAsync(
            BuildStartBody(category, priority, tagSuffix: "list-filter")
        );
        await _wf.WaitForStateAsync(instanceId, "active-state", ShortStateTimeout);
        return instanceId;
    }

    private static object BuildStartBody(string? category, int? priority, string tagSuffix)
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey($"instance-mgmt-{tagSuffix}");
        var tags = new[] { "integration-test", "instance-management", tagSuffix };

        if (category is null && priority is null)
            return new
            {
                key,
                tags,
            };

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

    private async Task RunThreeStepTransitionAsync(
        string instanceId,
        string processingTransition,
        string expectedFinalState
    )
    {
        await _wf.WaitForStateAsync(instanceId, "active-state", ShortStateTimeout);
        await _wf.RunTransitionAsync(instanceId, "process", headers: null);
        await _wf.WaitForStateAsync(instanceId, "processing-state", ShortStateTimeout);
        await _wf.RunTransitionAsync(instanceId, processingTransition, headers: null);
        await _wf.WaitForStateAsync(instanceId, expectedFinalState, ShortStateTimeout);

        var body = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        // On reaching a final state, status should be "C" (happy-path terminal).
        Assert.Equal("C", StateFunctionJson.ExtractStatus(body));
    }

    private async Task<JsonElement> ListInstancesWithFilterAsync(string filterJson)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["filter"] = filterJson,
            // Large page size in test env — avoid coupling with pagination tests.
            ["pageSize"] = "100",
            ["sort"] = "-createdAt",
        };
        var response = await Api.ListInstancesAsync(WorkflowKey, queryParams);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Body;
    }

    private static int IndexOfInstance(JsonElement body, string instanceId)
    {
        var items = InstanceListJson.ExtractItems(body);
        for (int i = 0; i < items.Count; i++)
        {
            if (
                string.Equals(
                    InstanceListJson.TryGetId(items[i]),
                    instanceId,
                    StringComparison.Ordinal
                )
            )
                return i;
        }
        return -1;
    }

    private async Task<JsonElement> GetAttributesAsync(string workflowKey, string instanceId)
    {
        var response = await Api.GetInstanceAsync(workflowKey, instanceId);
        Assert.True(
            response.Body.TryGetProperty("attributes", out var attributes),
            "GetInstance response should include 'attributes'."
        );
        return attributes;
    }
}
