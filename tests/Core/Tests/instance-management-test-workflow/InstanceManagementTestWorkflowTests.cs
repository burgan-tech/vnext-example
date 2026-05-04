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
///   <item>Pagination (page / pageSize); list sort by <c>createdAt</c> using runtime <c>sort</c> / <c>orderBy</c> JSON (<c>vnext-runtime/doc/tr/flow/instance-filtering.md</c>, OrderBy / Sort)</item>
///   <item><c>InitInstanceMgmtMapping</c> contract (category / priority / testStarted / startedAt)</item>
/// </list>
///
/// Layout:
/// <list type="bullet">
///   <item>Generic primitives live in <see cref="WorkflowInstanceTestHelper"/> /
///         <see cref="InstanceListJson"/> / <see cref="JsonElementAssertions"/>.</item>
///   <item>Workflow-specific start-body shape and state literals live in
///         <see cref="InstanceManagementScenarioActions"/>; instance <c>attributes</c> contract
///         assertions live in <see cref="InstanceManagementInstanceDataAssertions"/>.</item>
/// </list>
/// </summary>
public class InstanceManagementTestWorkflowTests : IntegrationTestBase
{
    private const string WorkflowKey = "instance-management-test-workflow";

    /// <summary>
    /// Runtime <c>sort</c> query shape (v0.0.37+): <c>?sort={"field":"createdAt","direction":"desc"}</c>
    /// — see <c>vnext-runtime/doc/tr/flow/instance-filtering.md</c> (OrderBy / Sort).
    /// </summary>
    private const string SortCreatedAtDesc = "{\"field\":\"createdAt\",\"direction\":\"desc\"}";

    /// <summary>Same as <see cref="SortCreatedAtDesc"/> with <c>direction: asc</c> (default when omitted at API level).</summary>
    private const string SortCreatedAtAsc = "{\"field\":\"createdAt\",\"direction\":\"asc\"}";

    /// <summary>Tolerance window for short manual-transition flows.</summary>
    private static readonly TimeSpan ShortStateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Workflow timeout is <c>PT120S</c> (2 minutes). Conservative upper bound ~3 minutes for slow CI.
    /// (No lower bound; PollStateUntilAnyAsync does not short-circuit the “did the timer actually wait?” case.)
    /// </summary>
    private static readonly TimeSpan WorkflowTimeoutWait = TimeSpan.FromMinutes(3);

    private readonly WorkflowInstanceTestHelper _wf;
    private readonly InstanceManagementScenarioActions _scenario;

    public InstanceManagementTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _wf = new WorkflowInstanceTestHelper(Api, WorkflowKey);
        _scenario = new InstanceManagementScenarioActions(_wf, ShortStateTimeout);
    }

    // -----------------------------------------------------------------------
    //  Transition variants + final state subTypes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ProcessThenFinish_ReachesCompletedState()
    {
        var instanceId = await _scenario.StartDefaultInstanceAsync("finance", 1);
        await _wf.WaitForStateAsync(instanceId, "active-state", ShortStateTimeout);

        await _wf.RunTransitionAsync(instanceId, "process", headers: null);
        await _wf.WaitForStateAsync(instanceId, "processing-state", ShortStateTimeout);

        await _wf.RunTransitionAsync(instanceId, "finish", headers: null);
        await _wf.WaitForStateAsync(instanceId, "completed-state", ShortStateTimeout);

        // completed-state subType:1 → happy-path final: status "C" (Completed).
        var body = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(body));

        var attrs = await _wf.GetAttributesAsync(instanceId);
        InstanceManagementInstanceDataAssertions.AssertInitialAttributes(
            attrs,
            expectedCategory: "finance",
            expectedPriority: 1
        );
    }

    [Fact]
    public async Task FastComplete_FromActive_ReachesCompletedStateWithoutProcessing()
    {
        var instanceId = await _scenario.StartDefaultInstanceAsync(category: null, priority: null);

        await _wf.WaitForStateAsync(instanceId, "active-state", ShortStateTimeout);
        await _wf.RunTransitionAsync(instanceId, "fast-complete", headers: null);
        await _wf.WaitForStateAsync(instanceId, "completed-state", ShortStateTimeout);

        var body = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(body));

        // When category/priority are omitted from the start body, init mapping applies defaults.
        var attrs = await _wf.GetAttributesAsync(instanceId);
        InstanceManagementInstanceDataAssertions.AssertInitialAttributes(
            attrs,
            expectedCategory: "default",
            expectedPriority: 1
        );
    }

    [Fact]
    public async Task RejectPath_ReachesRejectedState_SubType2_StillCompleted()
    {
        var instanceId = await _scenario.StartDefaultInstanceAsync(category: "risky", priority: 3);

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
        var instanceId = await _scenario.StartDefaultInstanceAsync(category: "ops", priority: 2);
        await _scenario.RunThreeStepTransitionAsync(instanceId, "suspend", "suspended-state");

        // subType:4 (Temporarily Suspended) — runtime state subType filtering treats this final state as Suspended (see instance-filtering.md "Instance Subtype (4,5,6) Filtering").
    }

    [Fact]
    public async Task BusyPath_ReachesBusyState_SubType5()
    {
        var instanceId = await _scenario.StartDefaultInstanceAsync(category: "ops", priority: 2);
        await _scenario.RunThreeStepTransitionAsync(instanceId, "set-busy", "busy-state");
    }

    [Fact]
    public async Task HumanPath_ReachesHumanState_SubType6()
    {
        var instanceId = await _scenario.StartDefaultInstanceAsync(category: "ops", priority: 2);
        await _scenario.RunThreeStepTransitionAsync(instanceId, "assign-human", "human-state");
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
        var financeId = await _scenario.StartAndAdvanceToActiveAsync(
            category: "finance",
            priority: 1
        );
        var opsId = await _scenario.StartAndAdvanceToActiveAsync(category: "ops", priority: 2);

        // filter={"attributes":{"category":{"eq":"finance"}}}
        var body = await _wf.ListInstancesAsync(
            filterJson: "{\"attributes\":{\"category\":{\"eq\":\"finance\"}}}",
            sort: SortCreatedAtDesc,
            pageSize: 100
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
        var activeId = await _scenario.StartAndAdvanceToActiveAsync(
            category: "finance",
            priority: 1
        );
        var completedId = await _scenario.StartAndAdvanceToActiveAsync(
            category: "ops",
            priority: 1
        );
        await _wf.RunTransitionAsync(completedId, "fast-complete", headers: null);
        await _wf.WaitForStateAsync(completedId, "completed-state", ShortStateTimeout);

        // filter={"currentState":{"eq":"active-state"}}
        var body = await _wf.ListInstancesAsync(
            filterJson: "{\"currentState\":{\"eq\":\"active-state\"}}",
            sort: SortCreatedAtDesc,
            pageSize: 100
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
        var activeId = await _scenario.StartAndAdvanceToActiveAsync(
            category: "finance",
            priority: 1
        );
        var completedId = await _scenario.StartAndAdvanceToActiveAsync(
            category: "ops",
            priority: 1
        );
        await _wf.RunTransitionAsync(completedId, "fast-complete", headers: null);
        await _wf.WaitForStateAsync(completedId, "completed-state", ShortStateTimeout);

        // filter={"status":{"eq":"A"}} — status code "A" (Active).
        var body = await _wf.ListInstancesAsync(
            filterJson: "{\"status\":{\"eq\":\"A\"}}",
            sort: SortCreatedAtDesc,
            pageSize: 100
        );

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
        var sortTag = $"sort-desc-{Guid.NewGuid():N}";
        var firstId = await _scenario.StartAndAdvanceToActiveAsync(category: sortTag, priority: 1);
        await Task.Delay(1200);
        var secondId = await _scenario.StartAndAdvanceToActiveAsync(category: sortTag, priority: 1);

        var body = await _wf.ListInstancesAsync(
            filterJson: $"{{\"attributes\":{{\"category\":{{\"eq\":\"{sortTag}\"}}}}}}",
            sort: SortCreatedAtDesc,
            pageSize: 100
        );

        var firstIdx = InstanceListJson.IndexOfInstanceId(body, firstId);
        var secondIdx = InstanceListJson.IndexOfInstanceId(body, secondId);

        Assert.True(firstIdx >= 0, $"first instance ({firstId}) should be listed.");
        Assert.True(secondIdx >= 0, $"second instance ({secondId}) should be listed.");
        Assert.True(
            secondIdx < firstIdx,
            "Newest instance (second) should appear before the older one under sort with createdAt desc; "
                + $"secondIdx={secondIdx}, firstIdx={firstIdx}."
        );
    }

    [Fact]
    public async Task Sort_AscendingByCreatedAt_OldestInstanceAppearsBeforeNewest()
    {
        var sortTag = $"sort-asc-{Guid.NewGuid():N}";
        var firstId = await _scenario.StartAndAdvanceToActiveAsync(category: sortTag, priority: 1);
        await Task.Delay(1200);
        var secondId = await _scenario.StartAndAdvanceToActiveAsync(category: sortTag, priority: 1);

        var body = await _wf.ListInstancesAsync(
            filterJson: $"{{\"attributes\":{{\"category\":{{\"eq\":\"{sortTag}\"}}}}}}",
            sort: SortCreatedAtAsc,
            pageSize: 100
        );

        var firstIdx = InstanceListJson.IndexOfInstanceId(body, firstId);
        var secondIdx = InstanceListJson.IndexOfInstanceId(body, secondId);

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
            await _scenario.StartAndAdvanceToActiveAsync(category: "finance", priority: 1),
            await _scenario.StartAndAdvanceToActiveAsync(category: "finance", priority: 1),
            await _scenario.StartAndAdvanceToActiveAsync(category: "finance", priority: 1),
        };

        var body = await _wf.ListInstancesAsync(
            filterJson: "{\"attributes\":{\"category\":{\"eq\":\"finance\"}}}",
            sort: SortCreatedAtDesc,
            page: 1,
            pageSize: 2
        );

        var items = InstanceListJson.ExtractItems(body);
        Assert.True(
            items.Count <= 2,
            $"page=1&pageSize=2 should cap returned items at 2; got {items.Count}."
        );

        // If a pagination object exists, page/pageSize should be reported correctly.
        var page = InstanceListJson.TryGetPaginationInt(body, "page");
        var pageSize = InstanceListJson.TryGetPaginationInt(body, "pageSize");
        var totalCount = InstanceListJson.TryGetPaginationInt(body, "totalCount");
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
        var instanceId = await _scenario.StartAndAdvanceToActiveAsync(
            category: "timeout-test",
            priority: 1
        );

        // If we leave before 120s the assertion is meaningless; tolerate up to ~3 min poll.
        await _wf.WaitForStateAsync(instanceId, "timeout-state", WorkflowTimeoutWait);

        var body = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        // timeout-state is final so status "C" (Completed) is expected
        // (F = Faulted; no exception here — runtime treats timeout as normal terminal).
        Assert.Equal("C", StateFunctionJson.ExtractStatus(body));
    }
}
