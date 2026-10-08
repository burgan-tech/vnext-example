using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.HistoryNoneSubflowLab;

/// <summary>
/// history-none-subflow-lab (vnext#1006): a <c>history: none</c> parent requires a <c>history: none</c>
/// SubFlow child; a full-history parent may start a none child. The refused-child case starts with
/// sync=false so the child's refusal never becomes the client's own response.
/// </summary>
public class HistoryNoneSubflowLabTests : WorkflowTestBase
{
    public HistoryNoneSubflowLabTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task NoneParent_NoneChild_Completes_AndTheHandoffCarriesTheParentData()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var parentId = await StartAsync("hns-parent", new { token });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync("hns-parent", parentId)).Status == "C",
            $"hns-parent never completed — {await DescribeAsync("hns-parent", parentId)}", TimeSpan.FromSeconds(60));

        var parent = await GetAttributesAsync("hns-parent", parentId);
        // parentToken lived only in the parent's buffer until the handoff write; the child's input
        // mapping reads the parent from the database, so the echo proves the handoff flush.
        Assert.Equal($"tok-{token}", parent.GetProperty("parentToken").GetString());
        Assert.Equal($"tok-{token}", parent.GetProperty("childResult").GetString());
        Assert.Empty(await HistoryAsync("hns-parent", parentId));
    }

    [Fact]
    public async Task NoneParent_FullChild_FaultsTheParent()
    {
        // Async start: with sync=true the child's refusal would become this request's own answer.
        var (startStatus, startBody) = await SendRawAsync(HttpMethod.Post,
            "api/v1/core/workflows/hns-parent-bad/instances/start?sync=false", new { token = "bad" }, Headers());
        Assert.True((int)startStatus < 300, $"start answered {(int)startStatus}: {startBody}");
        var parentId = JsonDocument.Parse(startBody).RootElement.GetProperty("id").GetString()!;

        await WaitUntilAsync(async () => (await GetInstanceStateAsync("hns-parent-bad", parentId)).Status == "F",
            $"hns-parent-bad never faulted — {await DescribeAsync("hns-parent-bad", parentId)}", TimeSpan.FromSeconds(60));

        Assert.Equal("hns-sub", (await GetInstanceStateAsync("hns-parent-bad", parentId)).State);
        var (status, incident) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/hns-parent-bad/instances/{parentId}/incidents/active", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"no active incident: {incident}");
        Assert.Contains("Instance:100044", incident, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullParent_NoneChild_Completes()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var parentId = await StartAsync("hns-parent-full", new { token });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync("hns-parent-full", parentId)).Status == "C",
            $"hns-parent-full never completed — {await DescribeAsync("hns-parent-full", parentId)}", TimeSpan.FromSeconds(60));

        Assert.Equal($"tok-{token}", (await GetAttributesAsync("hns-parent-full", parentId)).GetProperty("childResult").GetString());
        Assert.NotEmpty(await HistoryAsync("hns-parent-full", parentId));
    }

    private async Task<List<JsonElement>> HistoryAsync(string workflow, string id)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{workflow}/instances/{id}/transitions", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"history answered {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("transitions").EnumerateArray().ToList();
    }
}
