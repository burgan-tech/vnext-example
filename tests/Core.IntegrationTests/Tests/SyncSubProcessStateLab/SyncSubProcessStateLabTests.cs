using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SyncSubProcessStateLab;

/// <summary>
/// sync-subprocess-state-lab (vnext#1182): a sync start/transition response carries the state the
/// instance came to rest in (<c>currentState</c>, <c>effectiveState</c>) next to its status, and a
/// sync SubProcess task's output therefore holds the child's state — no extra instance GET or
/// state-function call. An async 202 carries neither.
/// </summary>
public class SyncSubProcessStateLabTests : WorkflowTestBase
{
    private const string Parent = "ssp-parent";
    private const string Child = "ssp-child";

    public SyncSubProcessStateLabTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task SyncSubProcess_TaskOutputCarriesTheChildsRestState()
    {
        var parentId = await StartAsync(Parent, new { });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Parent, parentId)).State == "spawned",
            $"ssp-parent never reached 'spawned' — {await DescribeAsync(Parent, parentId)}",
            TimeSpan.FromSeconds(60));
        await AssertNotFaultedAsync(Parent, parentId);

        var data = await GetAttributesAsync(Parent, parentId);
        var childId = data.GetProperty("childId").GetString();
        Assert.False(string.IsNullOrEmpty(childId), "the SubProcess output carried no child id");

        // The child auto-chained out of ssp-received; the output reports where it came to rest.
        Assert.Equal("ssp-otp-waiting", data.GetProperty("childCurrentState").GetString());
        Assert.Equal("ssp-otp-waiting", data.GetProperty("childEffectiveState").GetString());
        Assert.Equal("ssp-otp-waiting", (await GetInstanceStateAsync(Child, childId!)).State);
    }

    [Fact]
    public async Task SyncStartAndTransition_ResponsesCarryTheRestState()
    {
        var (startStatus, startBody) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{Child}/instances/start?sync=true", new { }, Headers());
        Assert.True(startStatus == HttpStatusCode.OK, $"sync start answered {(int)startStatus}: {startBody}");

        var start = JsonDocument.Parse(startBody).RootElement;
        var childId = start.GetProperty("id").GetString()!;
        Assert.Equal("ssp-otp-waiting", start.GetProperty("currentState").GetString());
        Assert.Equal("ssp-otp-waiting", start.GetProperty("effectiveState").GetString());

        var (transitionStatus, transitionBody) = await SendRawAsync(HttpMethod.Patch,
            $"api/v1/core/workflows/{Child}/instances/{childId}/transitions/approve?sync=true", new { }, Headers());
        Assert.True(transitionStatus == HttpStatusCode.OK,
            $"sync transition answered {(int)transitionStatus}: {transitionBody}");

        var transition = JsonDocument.Parse(transitionBody).RootElement;
        Assert.Equal("ssp-approved", transition.GetProperty("currentState").GetString());
        Assert.Equal("ssp-approved", transition.GetProperty("effectiveState").GetString());
    }

    [Fact]
    public async Task AsyncStart_ResponseStaysIdentityOnly()
    {
        var (status, body) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{Child}/instances/start?sync=false", new { }, Headers());
        Assert.True((int)status < 300, $"async start answered {(int)status}: {body}");

        var root = JsonDocument.Parse(body).RootElement;
        Assert.False(root.TryGetProperty("currentState", out _), $"async start carried currentState: {body}");
        Assert.False(root.TryGetProperty("effectiveState", out _), $"async start carried effectiveState: {body}");
    }
}
