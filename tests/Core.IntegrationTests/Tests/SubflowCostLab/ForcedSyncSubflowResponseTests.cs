using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SubflowCostLab;

/// <summary>
/// subflow-cost-lab-forced-sync: a manual transition the definition forces to sync
/// (<c>executionType: S</c>) that enters a SubFlow state. The runtime answers it synchronously
/// whatever <c>sync</c> the caller sent, so the 200 body must carry the instance's attributes in both
/// cases. Before the fix a caller that sent <c>sync=false</c> got 200 with no attributes: the parent's
/// post-commit settlement loaded the data row by the caller's mode, and the response was projected
/// from that settlement.
/// </summary>
public class ForcedSyncSubflowResponseTests : WorkflowTestBase
{
    private const string Flow = "subflow-cost-lab-forced-sync";

    public ForcedSyncSubflowResponseTests(VNextTestEnvironment environment) : base(environment) { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedSyncTransitionIntoASubFlow_AnswersWithTheAttributes_WhateverTheCallerSent(bool callerSync)
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var id = await StartAsync(Flow, new { userName = token, fromStart = token });

        var (status, raw) = await SendRawAsync(HttpMethod.Patch,
            $"api/v1/core/workflows/{Flow}/instances/{id}/transitions/enter-sub?sync={(callerSync ? "true" : "false")}",
            new { fromTransition = token }, Headers());

        // 200, not 202: the definition decided sync, so the run finished before the answer.
        Assert.True(status == HttpStatusCode.OK, $"enter-sub answered {(int)status}: {raw}");
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("f-mid", body.GetProperty("currentState").GetString());
        Assert.True(body.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object,
            $"the sync answer carries no attributes: {raw}");
        Assert.Equal(token, attributes.GetProperty("fromStart").GetString());
        Assert.Equal(token, attributes.GetProperty("fromTransition").GetString());
        // Written by the child's output mapping during the SubFlow hop: the body is the settled instance.
        Assert.True(attributes.GetProperty("childCompleted").GetBoolean(), raw);
    }
}
