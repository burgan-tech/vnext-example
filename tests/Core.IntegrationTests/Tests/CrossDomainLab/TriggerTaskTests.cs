using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// AC-07..AC-11: the six cross-domain trigger tasks (14, 11, 12, 19, 13, 15), each on its own parent
/// transition so a failure points at exactly one task type. All of them carry
/// <c>useDapr: true</c> and resolve <c>partner</c> through <c>DaprDomainDiscoveryProvider</c>.
/// </summary>
/// <remarks>
/// Each test walks the parent from the start up to its own step (the steps are sequential in the
/// flow), so the tests stay independent at the cost of repeating the earlier hops.
/// </remarks>
[Collection("VNextIntegration")]
public class TriggerTaskTests : CrossDomainLabTestBase, IClassFixture<CrossDomainLabFixture>
{
    public TriggerTaskTests(VNextTestEnvironment environment, CrossDomainLabFixture lab) : base(environment, lab) { }

    /// <summary>AC-07: SubProcess (14) starts xd-worker in partner and the parent does not wait for it.</summary>
    [SkippableFact]
    public async Task SubProcess_SpawnsPartnerWorker()
    {
        RequirePartner();
        var (parentId, testId, _) = await BringParentPastSubflowAsync("xd-spawn");

        await StepAsync(parentId, "spawn-subprocess", "xd-after-spawn");

        var attributes = await GetAttributesAsync(Parent, parentId);
        var workerId = Str(attributes, "workerInstanceId");
        Assert.False(string.IsNullOrWhiteSpace(workerId), $"workerInstanceId missing from parent data: {attributes}");

        await WaitForPartnerStateAsync(Worker, workerId!, "worker-done");
        var (_, _, _, workerAttributes) = await GetPartnerInstanceAsync(Worker, workerId!);
        Assert.Equal(testId, Str(workerAttributes, "testId"));
    }

    /// <summary>AC-08: Start (11, sync) creates xd-remote in partner and records its id.</summary>
    [SkippableFact]
    public async Task StartTask_StartsRemoteInstance()
    {
        RequirePartner();
        var (parentId, testId, _) = await BringParentPastSubflowAsync("xd-start");
        await StepAsync(parentId, "spawn-subprocess", "xd-after-spawn");

        await StepAsync(parentId, "start-remote", "xd-after-start");

        var attributes = await GetAttributesAsync(Parent, parentId);
        var remoteId = RemoteIdentifier(attributes);
        var (http, state, _, remoteAttributes) = await GetPartnerInstanceAsync(Remote, remoteId);
        Assert.Equal(HttpStatusCode.OK, http);
        Assert.Equal("remote-waiting", state);
        Assert.Equal(testId, Str(remoteAttributes, "testId"));
        Assert.Equal(parentId, Str(remoteAttributes, "parentInstanceId"));
    }

    /// <summary>AC-09: DirectTrigger (12) fires remote-advance on the partner instance.</summary>
    [SkippableFact]
    public async Task DirectTrigger_AdvancesRemoteInstance()
    {
        RequirePartner();
        var (parentId, _, _) = await BringParentPastSubflowAsync("xd-trigger");
        await StepAsync(parentId, "spawn-subprocess", "xd-after-spawn");
        await StepAsync(parentId, "start-remote", "xd-after-start");

        await StepAsync(parentId, "trigger-remote", "xd-after-trigger");

        var remoteId = RemoteIdentifier(await GetAttributesAsync(Parent, parentId));
        await WaitForPartnerStateAsync(Remote, remoteId, "remote-done");
        var (_, _, _, remoteAttributes) = await GetPartnerInstanceAsync(Remote, remoteId);
        Assert.Equal("xd-parent", Str(remoteAttributes, "advancedBy"));
    }

    /// <summary>AC-10: GetInstance (19) and GetInstanceData (13) read the partner instance back into the parent.</summary>
    [SkippableFact]
    public async Task GetInstance_And_GetInstanceData_ReadRemote()
    {
        RequirePartner();
        var (parentId, testId, _) = await BringParentPastSubflowAsync("xd-read");
        await StepAsync(parentId, "spawn-subprocess", "xd-after-spawn");
        await StepAsync(parentId, "start-remote", "xd-after-start");
        await StepAsync(parentId, "trigger-remote", "xd-after-trigger");

        await StepAsync(parentId, "read-remote", "xd-after-read");

        var attributes = await GetAttributesAsync(Parent, parentId);
        Assert.Equal("remote-done", Str(attributes, "remoteState"));
        Assert.True(attributes.TryGetProperty("remoteData", out var remoteData) && remoteData.ValueKind == JsonValueKind.Object,
            $"remoteData missing from parent data: {attributes}");
        Assert.Equal(testId, Str(remoteData, "testId"));
    }

    /// <summary>AC-11: GetInstances (15) with a structured attributes.testId filter finds the partner instance; the parent completes.</summary>
    [SkippableFact]
    public async Task GetInstances_FiltersRemoteByTestId()
    {
        RequirePartner();
        var (parentId, testId, _) = await BringParentPastSubflowAsync("xd-list");
        await StepAsync(parentId, "spawn-subprocess", "xd-after-spawn");
        await StepAsync(parentId, "start-remote", "xd-after-start");
        await StepAsync(parentId, "trigger-remote", "xd-after-trigger");
        await StepAsync(parentId, "read-remote", "xd-after-read");

        await StepAsync(parentId, "list-remote", "xd-completed");

        var attributes = await GetAttributesAsync(Parent, parentId);
        Assert.True(attributes.GetProperty("remoteCount").GetInt32() >= 1, $"remoteCount < 1: {attributes}");
        var ids = attributes.GetProperty("remoteListTestIds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.Equal(testId, id));

        var (_, status) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("C", status);
    }

    /// <summary>The start mapping records the id when the response exposed one, and always the deterministic key.</summary>
    private static string RemoteIdentifier(JsonElement attributes)
    {
        var id = Str(attributes, "remoteInstanceId");
        if (!string.IsNullOrWhiteSpace(id)) return id!;
        var key = Str(attributes, "remoteKey");
        Assert.False(string.IsNullOrWhiteSpace(key), $"neither remoteInstanceId nor remoteKey in parent data: {attributes}");
        return key!;
    }
}
