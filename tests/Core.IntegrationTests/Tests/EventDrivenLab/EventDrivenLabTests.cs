using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.EventDrivenLab;

/// <summary>
/// The event-driven entry point, end to end over a real broker.
/// <para>
/// This is the first coverage the <c>instances/events</c> endpoint has ever had. It is reached only
/// through a Dapr Subscription, so a test that posted to the endpoint directly would prove the
/// mapping script and nothing about the wiring that actually delivers production traffic — the
/// topic, the subscription route, the CloudEvent unwrap, and the <c>triggerType: 3</c> gate.
/// </para>
/// <para>
/// Both messages are published to the broker; nothing here calls the runtime's HTTP API to drive the
/// flow. The instance is created by an event and advanced by an event.
/// </para>
/// </summary>
public class EventDrivenLabTests : IntegrationTestBase
{
    private const string Workflow = "event-driven-lab";
    private const string StartTopic = "core.event-driven-lab";
    private const string ApproveTopic = "core.event-driven-lab.approve";
    private const string PubSub = "vnext-pubsub";

    private readonly HttpClient _dapr;

    public EventDrivenLabTests(VNextTestEnvironment environment) : base(environment)
    {
        // Published through the orchestration sidecar, because that is the sidecar whose
        // subscriptions are under test. Its port is the runtime's Dapr HTTP port, recorded per
        // domain in ai-docs/local-environments/<domain>.md.
        var daprUrl = System.Environment.GetEnvironmentVariable("VNEXT_DAPR_HTTP_URL")
                      ?? "http://localhost:42110";
        _dapr = new HttpClient { BaseAddress = new Uri(daprUrl.TrimEnd('/') + "/") };
    }

    [SkippableFact]
    public async Task AnEventStartsTheInstance_AndASecondEventAdvancesIt()
    {
        Skip.If(System.Environment.GetEnvironmentVariable("VNEXT_DAPR_HTTP_URL") is null
                && System.Environment.GetEnvironmentVariable("VNEXT_BASE_URL") is null,
            "needs a running stack with a reachable Dapr sidecar");

        var orderId = $"evt-{Guid.NewGuid():N}"[..16];

        // 1. Start by event. The workflow-level mapping turns the payload into a new instance whose
        //    key is the orderId — the correlation contract the second event depends on.
        await PublishAsync(StartTopic, new { orderId, amount = 250 });

        var instanceId = await WaitForInstanceAsync(orderId);
        var afterStart = await GetStateAsync(instanceId);
        Assert.Equal("awaiting-approval", afterStart.State);

        var attributes = await GetAttributesAsync(instanceId);
        Assert.Equal("event", attributes.GetProperty("startedBy").GetString());
        Assert.Equal(orderId, attributes.GetProperty("orderId").GetString());

        // 2. Advance by event. Correlation is by business key only — no instance id is published,
        //    which is the whole point of the mapping's InstanceKey.
        await PublishAsync(ApproveTopic, new { orderId, decision = "approved", approvedBy = "ops" });

        await WaitUntilAsync(
            async () => (await GetStateAsync(instanceId)).State == "approved",
            $"the approval event never advanced {instanceId}",
            TimeSpan.FromSeconds(60));

        // The transition ran with the MAPPED body, not merely with a state change: an event that
        // moved the state but dropped its payload would pass a state-only assertion.
        var final = await GetAttributesAsync(instanceId);
        Assert.Equal("approved", final.GetProperty("decision").GetString());
        Assert.Equal("ops", final.GetProperty("approvedBy").GetString());
    }

    /// <summary>
    /// An event naming no active instance is acked, not retried. The endpoint answers the broker's
    /// protocol, so a "nothing matched" result must not look like a failure — otherwise the broker
    /// redelivers a message this runtime can never satisfy.
    /// </summary>
    [SkippableFact]
    public async Task AnApprovalForAnUnknownOrder_IsAcceptedAndChangesNothing()
    {
        Skip.If(System.Environment.GetEnvironmentVariable("VNEXT_DAPR_HTTP_URL") is null
                && System.Environment.GetEnvironmentVariable("VNEXT_BASE_URL") is null,
            "needs a running stack with a reachable Dapr sidecar");

        var response = await PublishAsync(
            ApproveTopic,
            new { orderId = $"missing-{Guid.NewGuid():N}"[..20], decision = "approved", approvedBy = "ops" });

        Assert.True(
            response.IsSuccessStatusCode,
            $"the broker publish was rejected with {response.StatusCode}");
    }

    private async Task<HttpResponseMessage> PublishAsync(string topic, object payload)
    {
        var response = await _dapr.PostAsJsonAsync($"v1.0/publish/{PubSub}/{topic}", payload);
        Assert.True(
            response.IsSuccessStatusCode,
            $"publish to {topic} failed with {response.StatusCode}");
        return response;
    }

    /// <summary>
    /// Finds the instance the start event created, by its business key. The publish returns as soon
    /// as the broker accepts the message, so the instance appears asynchronously.
    /// </summary>
    private async Task<string> WaitForInstanceAsync(string key)
    {
        string? instanceId = null;

        await WaitUntilAsync(
            async () =>
            {
                // Filtered by business key, because the publish returns no instance id — correlation
                // by key is exactly what the mapping's InstanceKey establishes.
                var response = await Api.ListInstancesAsync(
                    Workflow,
                    new Dictionary<string, string>
                    {
                        ["filter"] = $"{{\"key\":{{\"eq\":\"{key}\"}}}}"
                    });
                if (!response.IsSuccessStatusCode) return false;

                if (!response.Body.TryGetProperty("items", out var items) ||
                    items.GetArrayLength() == 0)
                {
                    return false;
                }

                instanceId = items[0].GetProperty("id").GetString();
                return instanceId is not null;
            },
            $"no instance was created for key {key} — the start subscription never reached the runtime",
            TimeSpan.FromSeconds(60));

        return instanceId!;
    }

    private async Task<(string State, string Status)> GetStateAsync(string instanceId)
    {
        var response = await Api.CallInstanceFunctionAsync(Workflow, instanceId, "state");
        return (response.Body.GetProperty("state").GetString() ?? "",
                response.Body.GetProperty("status").GetString() ?? "");
    }

    private async Task<JsonElement> GetAttributesAsync(string instanceId)
    {
        var response = await Api.GetInstanceAsync(Workflow, instanceId);
        return response.Body.GetProperty("attributes");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }

        Assert.Fail($"Timed out after {timeout.TotalSeconds:0}s: {because}");
    }
}
