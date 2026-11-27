using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.StartRollbackLab;

/// <summary>
/// start-rollback-lab: a start that fails AFTER its instance row was inserted must leave nothing
/// behind. The runtime inserts the row, then runs the start mapping and appends the initial data
/// version, all in one transaction. Before the fix that transaction was committed even when the start
/// mapping failed, so the instance stayed, without its data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Root start.</b> The leftover instance kept the start's key. The idempotency probe then answered
/// a retry with that empty instance, as if it had started; a real one was never created.
/// </para>
/// <para>
/// <b>Child started by a parent.</b> The leftover child stayed behind, without its data, and the
/// parent's fault faulted it too (<c>child-fault</c>). The parent's retry probes the child before
/// restarting it: it found a faulted child, unfaulted the parent (resolving its incident) and handed
/// the retry to that child, which could not succeed. The parent was left Active, with no incident,
/// waiting on a dead child.
/// </para>
/// <para>
/// <b>Child started through <c>sub/instances/start</c></b> (a cross-domain parent's call, with a
/// pre-assigned id and strict idempotency). A SubFlow child is created Busy, and no parent faulted the
/// leftover here, so it stayed Busy; a second start with the same id was refused with
/// <c>409 ActiveInstanceAlreadyExists</c>.
/// </para>
/// <para>
/// <b>Process started by a SubProcess task.</b> The same start, reached through the parent's
/// <c>SubProcess</c> task with a pre-assigned id: a failed start mapping fails the task and faults the
/// parent, and must leave no process instance behind.
/// </para>
/// <para>
/// One class on purpose: the child and process checks compare that flow's instance list before and
/// after a parent run, so nothing else may start instances of those flows at the same time.
/// </para>
/// </remarks>
public class StartRollbackLabTests : WorkflowTestBase
{
    private const string Root = "start-rollback-lab-root";
    private const string Parent = "start-rollback-lab-parent";
    private const string Child = "start-rollback-lab-child";
    private const string SpParent = "start-rollback-lab-sp-parent";
    private const string Process = "start-rollback-lab-process";

    public StartRollbackLabTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task RootStart_WithACustomer_CommitsTheInstanceWithItsMappedData()
    {
        var key = NewKey();

        var (status, body) = await StartRootAsync(new { key, attributes = new { customerId = "c-1" } });

        Assert.True((int)status < 400, $"start was refused with {(int)status}: {body}");
        var (getStatus, instance) = await GetAsync(Root, key);
        Assert.Equal(HttpStatusCode.OK, getStatus);
        AssertStartedWithMappedData(instance, "root-waiting", "c-1");
    }

    [Fact]
    public async Task RootStart_WhoseMappingFails_LeavesNoInstance_AndTheKeyStartsCleanlyAfterwards()
    {
        var key = NewKey();

        var (failedStatus, failedBody) = await StartRootAsync(new { key, attributes = new { note = "no customer" } });

        Assert.True((int)failedStatus >= 400, $"a start whose mapping throws must fail, got {(int)failedStatus}: {failedBody}");
        var (leftStatus, left) = await GetAsync(Root, key);
        Assert.True(leftStatus == HttpStatusCode.NotFound,
            $"the failed start left an instance behind under its key ({(int)leftStatus}): {left}");

        // The same key again, now with a customer: a real start, not the idempotent answer of a leftover.
        var (retryStatus, retryBody) = await StartRootAsync(new { key, attributes = new { customerId = "c-2" } });

        Assert.True((int)retryStatus < 400, $"the retry was refused with {(int)retryStatus}: {retryBody}");
        var (getStatus, instance) = await GetAsync(Root, key);
        Assert.Equal(HttpStatusCode.OK, getStatus);
        AssertStartedWithMappedData(instance, "root-waiting", "c-2");
        Assert.Equal(IdOf(retryBody), instance.GetProperty("id").GetString());
    }

    [Fact]
    public async Task SubStart_WithAnIdWhoseMappingFails_LeavesNoInstance_AndTheIdStartsCleanlyAfterwards()
    {
        var id = Guid.NewGuid().ToString();

        var (failedStatus, failedBody) = await StartSubAsync(id, new { note = "no customer" });

        Assert.True((int)failedStatus >= 400, $"a start whose mapping throws must fail, got {(int)failedStatus}: {failedBody}");
        var (leftStatus, left) = await GetAsync(Child, id);
        Assert.True(leftStatus == HttpStatusCode.NotFound,
            $"the failed start left an instance behind under its id ({(int)leftStatus}): {left}");

        var (retryStatus, retryBody) = await StartSubAsync(id, new { customerId = "c-3" });

        Assert.True((int)retryStatus < 400, $"the retry was refused with {(int)retryStatus}: {retryBody}");
        var (getStatus, instance) = await GetAsync(Child, id);
        Assert.Equal(HttpStatusCode.OK, getStatus);
        AssertStartedWithMappedData(instance, "child-waiting", "c-3");
    }

    [Fact]
    public async Task ChildStart_WithACustomer_StartsTheChild()
    {
        var parentId = await StartParentAsync(Parent, new { runId = NewKey(), customerId = "c-4" });

        await WaitUntilAsync(async () => await GetObservedStateAsync(Parent, parentId) == ("child-waiting", "A"),
            $"the child never reached child-waiting: {await DescribeAsync(Parent, parentId)}", TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task ChildStart_WhoseMappingFails_LeavesNoChild_AndTheParentFaults()
    {
        var childrenBefore = await ListIdsAsync(Child);

        var parentId = await StartParentAsync(Parent, new { runId = NewKey() });
        await WaitForFaultAsync(Parent, parentId);

        var (incidentStatus, incident) = await GetActiveIncidentAsync(Parent, parentId);
        Assert.True(incidentStatus == HttpStatusCode.OK, $"the faulted parent has no active incident: {incident}");
        AssertIsTheStartMappingFailure(incident);
        var newChildren = (await ListIdsAsync(Child)).Except(childrenBefore).ToList();
        Assert.True(newChildren.Count == 0,
            $"the failed child start left {newChildren.Count} child instance(s) behind: {string.Join(", ", newChildren)}");
    }

    [Fact]
    public async Task RetryingTheParent_RestartsTheChildStart_InsteadOfRetryingALeftoverChild()
    {
        var childrenBefore = await ListIdsAsync(Child);
        var parentId = await StartParentAsync(Parent, new { runId = NewKey() });
        await WaitForFaultAsync(Parent, parentId);
        var (_, firstIncident) = await GetActiveIncidentAsync(Parent, parentId);
        var firstIncidentId = TextOf(firstIncident, "id");

        // The parent's data still has no customer, so the restarted child start fails again. What
        // matters is that the retry restarts it and re-faults the parent with a fresh incident, instead
        // of handing the retry to a leftover child and leaving the parent Active with no incident.
        var (retryStatus, retryBody) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/{TestDomain.Name}/workflows/{Parent}/instances/{parentId}/retry?sync=true", new { }, Headers());

        Assert.True((int)retryStatus >= 400,
            $"the restart cannot succeed without a customer, yet the retry answered {(int)retryStatus}: {retryBody}");
        await WaitForFaultAsync(Parent, parentId);
        var (incidentStatus, secondIncident) = await GetActiveIncidentAsync(Parent, parentId);
        Assert.True(incidentStatus == HttpStatusCode.OK, $"the re-faulted parent has no active incident: {secondIncident}");
        Assert.NotEqual(firstIncidentId, TextOf(secondIncident, "id"));
        Assert.Empty((await ListIdsAsync(Child)).Except(childrenBefore));
    }

    [Fact]
    public async Task SubProcessStart_WithACustomer_StartsTheProcessWithItsMappedData()
    {
        var processesBefore = await ListIdsAsync(Process);

        var parentId = await StartParentAsync(SpParent, new { runId = NewKey(), customerId = "c-5" });

        string? processId = null;
        await WaitUntilAsync(async () =>
            {
                processId = (await ListIdsAsync(Process)).Except(processesBefore).SingleOrDefault();
                return processId != null &&
                       (await GetInstanceStateAsync(Process, processId)).Status == "A";
            },
            $"no started process appeared: {await DescribeAsync(SpParent, parentId)}", TimeSpan.FromSeconds(60));
        var (getStatus, process) = await GetAsync(Process, processId!);
        Assert.Equal(HttpStatusCode.OK, getStatus);
        AssertStartedWithMappedData(process, "process-waiting", "c-5");
        // The process starts in its own job (SetSync(false)); the parent commits its hop separately.
        await WaitUntilSettledAsync(SpParent, parentId);
        Assert.Equal("A", (await GetInstanceStateAsync(SpParent, parentId)).Status);
    }

    [Fact]
    public async Task SubProcessStart_WhoseMappingFails_LeavesNoProcess_AndTheParentFaults()
    {
        var processesBefore = await ListIdsAsync(Process);

        var parentId = await StartParentAsync(SpParent, new { runId = NewKey() });
        await WaitForFaultAsync(SpParent, parentId);

        var (incidentStatus, incident) = await GetActiveIncidentAsync(SpParent, parentId);
        Assert.True(incidentStatus == HttpStatusCode.OK, $"the faulted parent has no active incident: {incident}");
        AssertIsTheStartMappingFailure(incident);
        var newProcesses = (await ListIdsAsync(Process)).Except(processesBefore).ToList();
        Assert.True(newProcesses.Count == 0,
            $"the failed SubProcess start left {newProcesses.Count} process instance(s) behind: {string.Join(", ", newProcesses)}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string NewKey() => $"start-rollback-{Guid.NewGuid():N}"[..32];

    private Task<(HttpStatusCode Status, string Body)> StartRootAsync(object body) =>
        SendRawAsync(HttpMethod.Post, $"api/v1/{TestDomain.Name}/workflows/{Root}/instances/start?sync=true",
            body, Headers());

    /// <summary>
    /// The child start a cross-domain parent sends: a pre-assigned id and strict idempotency. A parent always
    /// sends <c>extraProperties</c> (its own metadata); the endpoint does not accept a body without it.
    /// </summary>
    private Task<(HttpStatusCode Status, string Body)> StartSubAsync(string id, object attributes) =>
        SendRawAsync(HttpMethod.Post, $"api/v1/{TestDomain.Name}/workflows/{Child}/sub/instances/start?sync=true",
            new { id, attributes, extraProperties = new { } }, Headers());

    /// <summary>
    /// Starts a parent with <c>sync=false</c>: with <c>sync=true</c> the child's failure would be the answer
    /// of the parent's own start call (see the SubflowStartFailureLab remarks).
    /// </summary>
    private async Task<string> StartParentAsync(string workflow, object attributes)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/start?sync=false", new { attributes }, Headers());
        Assert.True((int)status < 400, $"the parent start was refused with {(int)status}: {body}");
        return IdOf(body);
    }

    private async Task WaitForFaultAsync(string workflow, string parentId)
    {
        await WaitUntilAsync(async () => (await GetInstanceStateAsync(workflow, parentId)).Status == "F",
            $"the parent never faulted ({workflow}/{parentId})", TimeSpan.FromSeconds(60));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string workflow, string idOrKey)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{Uri.EscapeDataString(idOrKey)}",
            headers: Headers());
        return (status, Parse(body));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetActiveIncidentAsync(string workflow, string parentId)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{parentId}/incidents/active", headers: Headers());
        return (status, Parse(body));
    }

    private async Task<HashSet<string>> ListIdsAsync(string workflow)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances?pageSize=100", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"list {workflow} answered {(int)status}: {body}");
        return Parse(body).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToHashSet();
    }

    private static void AssertStartedWithMappedData(JsonElement instance, string state, string customerId)
    {
        var metadata = instance.GetProperty("metadata");
        Assert.Equal(state, metadata.GetProperty("currentState").GetString());
        Assert.Equal("A", metadata.GetProperty("status").GetString());
        var attributes = instance.GetProperty("attributes");
        Assert.True(attributes.ValueKind == JsonValueKind.Object, $"the instance has no data: {instance}");
        Assert.Equal(customerId, attributes.GetProperty("customerId").GetString());
        Assert.True(attributes.GetProperty("startMapped").GetBoolean());
    }

    /// <summary>The parent faulted because the start mapping threw, not for an unrelated reason.</summary>
    private static void AssertIsTheStartMappingFailure(JsonElement incident) =>
        Assert.Contains("the start payload has no customerId", TextOf(incident, "message"), StringComparison.Ordinal);

    private static string IdOf(string body) =>
        Parse(body).GetProperty("id").GetString() ?? throw new InvalidOperationException($"no id in: {body}");

    private static string TextOf(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "-"
            : "-";

    private static JsonElement Parse(string body) =>
        string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
}
