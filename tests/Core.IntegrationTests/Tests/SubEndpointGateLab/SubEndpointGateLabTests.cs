using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Core.IntegrationTests.Tests.SubEndpointGateLab;

/// <summary>
/// sub-endpoint-gate-lab (vnext#1265): the runtime-internal <c>sub/instances/start</c> and
/// <c>instances/{instance}/complete</c> endpoints behind the optional, domain-configured gate. A client
/// that calls them directly is refused before anything is written, while the runtime's own calls — which
/// prove themselves with a runtime-caller token — keep every SubFlow chain working, including the
/// user-less Inbox completion backup. Reuses the subflow-carried-headers-lab flows.
/// </summary>
/// <remarks>
/// Mode switch: the gate's mode cannot be read from outside, so the caller declares how the runtime was
/// started with <c>VNEXT_IT_SUB_ENDPOINT_GATE</c>:
/// <list type="bullet">
/// <item><c>enforce</c> — runtime started with a key ring (<c>Workflow__RuntimeCaller__KeyFile</c>) and
/// <c>Workflow__SubEndpointGate__Mode=Enforce</c>, <c>Grants__0__Role=$Everyone</c>, <c>Grants__0__Grant=deny</c>.</item>
/// <item><c>observe</c> — same with <c>Mode=Observe</c>.</item>
/// <item>unset — the default (gate Off).</item>
/// </list>
/// The chain tests run in every mode: whatever the gate does, the runtime's own calls must pass.
/// </remarks>
public class SubEndpointGateLabTests : WorkflowTestBase
{
    private const string Parent = "sfch-parent";
    private const string Child = "sfch-child";
    private const string ModeVariable = "VNEXT_IT_SUB_ENDPOINT_GATE";
    private const string RoleDenied = "Authorization:110001";
    private const string TokenHeader = "x-vnext-runtime-token";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    private readonly ITestOutputHelper _output;

    public SubEndpointGateLabTests(VNextTestEnvironment environment, ITestOutputHelper output)
        : base(environment)
    {
        _output = output;
    }

    private static string Mode => System.Environment.GetEnvironmentVariable(ModeVariable)?.Trim().ToLowerInvariant() ?? "off";

    [SkippableFact]
    public async Task Enforce_ClientSubStart_IsRefused_BeforeAnythingIsWritten()
    {
        Skip.IfNot(Mode == "enforce", $"{ModeVariable}={Mode} — needs a runtime started with the gate in Enforce");

        var childId = Guid.NewGuid().ToString();
        var (status, body) = await SubStartAsClientAsync(childId, forgedToken: "v1;k=k1;t=1;d=core;c=subflow;s=AAAA");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(RoleDenied, body, StringComparison.Ordinal);
        Assert.False(await ExistsAsync(Child, childId), $"a refused sub-start created child {childId}");
    }

    [SkippableFact]
    public async Task Enforce_ForgedCompletion_IsRefused_AndTheParentStaysPut()
    {
        Skip.IfNot(Mode == "enforce", $"{ModeVariable}={Mode} — needs a runtime started with the gate in Enforce");

        var (parentId, childId) = await StartParentWithParkedChildAsync();

        var (status, body) = await CompleteAsClientAsync(parentId, childId);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(RoleDenied, body, StringComparison.Ordinal);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(("sfch-a", "B"), await GetInstanceStateAsync(Parent, parentId));
        Assert.Equal("sfch-child-wait", (await GetInstanceStateAsync(Child, childId)).State);

        // The genuine path still settles the parent: child A finishes, child B runs, the parent completes.
        await FinishChildAsync(childId);
        await WaitParentStatusAsync(parentId, "C");
    }

    [SkippableFact]
    public async Task Observe_ClientSubStart_IsAdmitted()
    {
        Skip.IfNot(Mode == "observe", $"{ModeVariable}={Mode} — needs a runtime started with the gate in Observe");

        var childId = Guid.NewGuid().ToString();
        var (status, body) = await SubStartAsClientAsync(childId);

        Assert.True(status == HttpStatusCode.OK, $"Observe refused a sub-start with {(int)status}: {body}");
        Assert.True(await ExistsAsync(Child, childId), $"Observe sub-start did not create child {childId}");
    }

    [SkippableFact]
    public async Task Off_ClientSubStart_BehavesAsBefore()
    {
        Skip.IfNot(Mode == "off", $"{ModeVariable}={Mode} — the default (Off) behaviour is pinned only on a runtime without the gate");

        var childId = Guid.NewGuid().ToString();
        var (status, body) = await SubStartAsClientAsync(childId);

        Assert.True(status == HttpStatusCode.OK, $"an unconfigured runtime refused a sub-start with {(int)status}: {body}");
        Assert.True(await ExistsAsync(Child, childId), $"sub-start did not create child {childId}");
    }

    [Fact]
    public async Task RuntimeChain_ManualChild_StillCompletes()
    {
        var (parentId, childId) = await StartParentWithParkedChildAsync();

        await FinishChildAsync(childId);

        await WaitParentStatusAsync(parentId, "C");
    }

    [Fact]
    public async Task RuntimeChain_TimerChild_StillCompletes_WithNoUserOnTheCompletion()
    {
        // Child A settles by its own timer: the completion relay and the Inbox backup carry no user at all,
        // so only the runtime-caller token can get them through a gated `complete`.
        var parentId = await StartAsync(Parent, new { aMode = "timer" });
        _output.WriteLine($"parent={parentId} aMode=timer mode={Mode}");

        await WaitParentStatusAsync(parentId, "C");
    }

    [Fact]
    public async Task TokenHeader_SentByAClient_NeverReachesTheTransitionRecord()
    {
        // Every inbound header is persisted in the transition record and served by the history API; the
        // runtime-caller token header is removed at ingress, whatever the gate mode.
        var headers = Headers();
        headers[TokenHeader] = "client-supplied-token-value";
        var (status, body) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/{TestDomain.Name}/workflows/{Parent}/instances/start?sync=true",
            new { key = $"sfch-gate-{Guid.NewGuid():N}", attributes = new { aMode = "manual" } }, headers);
        Assert.True((int)status < 400, $"start refused with {(int)status}: {body}");
        var parentId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        var (historyStatus, history) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{Parent}/instances/{parentId}/transitions", headers: Headers());

        Assert.Equal(HttpStatusCode.OK, historyStatus);
        Assert.Contains("x-request-id", history, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TokenHeader, history, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client-supplied-token-value", history, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<(HttpStatusCode Status, string Body)> SubStartAsClientAsync(string childId, string? forgedToken = null)
    {
        var headers = Headers();
        if (forgedToken is not null) headers[TokenHeader] = forgedToken;
        return SendRawAsync(HttpMethod.Post,
            $"api/v1/{TestDomain.Name}/workflows/{Child}/sub/instances/start?sync=true",
            new
            {
                id = childId,
                key = $"sfch-gate-{Guid.NewGuid():N}",
                attributes = new { aMode = "manual" },
                extraProperties = new { }
            },
            headers);
    }

    private Task<(HttpStatusCode Status, string Body)> CompleteAsClientAsync(string parentId, string childId)
        => SendRawAsync(HttpMethod.Post,
            $"api/v1/{TestDomain.Name}/workflows/{Parent}/instances/{parentId}/complete",
            new
            {
                instanceId = parentId,
                domain = TestDomain.Name,
                flow = Parent,
                version = (string?)null,
                subInstanceId = childId,
                completedState = "sfch-child-done",
                instanceData = new { injectedByClient = true },
                completedAt = DateTime.UtcNow,
                sync = false
            },
            Headers());

    private async Task<(string ParentId, string ChildId)> StartParentWithParkedChildAsync()
    {
        var parentId = await StartAsync(Parent, new { aMode = "manual" });
        string? childId = null;
        await WaitUntilAsync(async () =>
            {
                var active = await GetActiveSubflowsAsync(Parent, parentId);
                if (!active.TryGetValue(Child, out childId)) return false;
                try
                {
                    return (await GetInstanceStateAsync(Child, childId)) is ("sfch-child-wait", "A");
                }
                catch (HttpRequestException exception) when (exception.Message.Contains("NotFound", StringComparison.Ordinal))
                {
                    return false;
                }
            },
            $"child A never parked in sfch-child-wait — {await DescribeAsync(Parent, parentId)}", Budget);
        _output.WriteLine($"parent={parentId} child={childId} mode={Mode}");
        return (parentId, childId!);
    }

    private async Task FinishChildAsync(string childId)
    {
        HttpStatusCode status = default;
        for (var attempt = 1; attempt <= LockContentionAttempts; attempt++)
        {
            status = await RunAsync(Child, childId, "finish");
            if (status != HttpStatusCode.Conflict) break;
            await Task.Delay(LockContentionDelay);
        }

        Assert.True((int)status < 400, $"'finish' on child {childId} was refused with {(int)status}");
    }

    private async Task WaitParentStatusAsync(string parentId, string status)
        => await WaitUntilAsync(async () => (await GetInstanceStateAsync(Parent, parentId)).Status == status,
            $"{Parent} never reached status {status} — {await DescribeAsync(Parent, parentId)}", Budget);

    private async Task<bool> ExistsAsync(string workflow, string instanceId)
    {
        var (status, _) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{instanceId}", headers: Headers());
        return status == HttpStatusCode.OK;
    }
}
