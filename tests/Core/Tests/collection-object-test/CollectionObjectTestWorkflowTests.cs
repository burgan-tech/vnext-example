using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;
using Xunit;

namespace Core.IntegrationTests.Tests.CollectionObjectTest;

/// <summary>
/// Integration tests for <c>collection-object-test-workflow</c>: linear pipeline of ScriptBase
/// collection/object API mappings with auto transitions to <c>test-completed-state</c>.
/// </summary>
public class CollectionObjectTestWorkflowTests : IntegrationTestBase
{
    private const string WorkflowKey = "collection-object-test-workflow";
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(60);
    private readonly WorkflowInstanceTestHelper _wf;

    public CollectionObjectTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _wf = new WorkflowInstanceTestHelper(Api, WorkflowKey);
    }

    [Fact]
    public async Task HappyPath_AllScriptBaseApis_CompleteSuccessfully()
    {
        var body = new
        {
            key = WorkflowInstanceTestHelper.UniqueInstanceKey("collection-object-test"),
            tags = new[] { "integration-test", "collection-object-test" },
            attributes = new { },
        };

        var instanceId = await _wf.StartInstanceIdAsync(body);

        await _wf.WaitForStateAsync(instanceId, "test-completed-state", StateTimeout);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(stateBody));

        var attrs = await _wf.GetAttributesAsync(instanceId);
        CollectionObjectInstanceDataAssertions.AssertFullHappyPathAttributes(attrs);
    }
}
