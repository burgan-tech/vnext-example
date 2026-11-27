using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Core.IntegrationTests.Tests.ErrorBoundaryLab;

namespace Core.IntegrationTests.Tests.DeclarativeMappingLab;

/// <summary>
/// Declarative event and output blocks (vnext #1229, phase C2): an event's <c>input</c> (document
/// <c>{ key, attributes }</c>, in place of an <c>IEventMapping</c> script), a workflow's <c>output</c> and a
/// function's <c>output</c> (document <c>{ data, statusCode, headers }</c>, in place of an output script).
/// Contract: vnext <c>docs/runtime/declarative-task-mappings.md</c> § "Event input, workflow output and
/// function output".
/// <para>
/// Flow <c>dml-event-output</c>: the workflow <c>event.input</c> starts an instance from an event, the
/// <c>approve-by-event</c> transition (<c>triggerType: 3</c>) correlates by the key its own <c>input</c> block
/// sets, and the workflow <c>output</c> shapes a sync start response. Functions <c>dml-fn-wrapped</c>
/// (<c>rawResponse: false</c>) and <c>dml-fn-raw</c> (<c>rawResponse: true</c>) run <c>dml-http-echo</c> twice
/// (slots <c>first</c> / <c>second</c>) and build the response from <c>result</c>.
/// </para>
/// <para>
/// Events are POSTed straight to <c>instances/events</c> — the Dapr subscription wiring is
/// <c>EventDrivenLab</c>'s subject; this scenario is about what the block makes of the payload.
/// </para>
/// </summary>
public class DeclarativeEventOutputTests : WorkflowTestBase
{
    private const string Workflow = "dml-event-output";

    private readonly HttpClient _http;

    public DeclarativeEventOutputTests(VNextTestEnvironment environment) : base(environment)
    {
        _http = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    /// <summary>
    /// The workflow <c>event.input</c> builds the start document: <c>$.key</c> from <c>input.orderId</c>,
    /// <c>select $.input.order into $.attributes.order</c>, constants and an event header
    /// (<c>headers["x-channel"]</c>). Only what the block wrote lands: the payload's <c>noise</c> does not.
    /// </summary>
    [Fact]
    public async Task StartEvent_InputBlock_CreatesInstance()
    {
        var orderId = NewKey("dml-evt");

        await PostEventAsync("start", null, new { orderId, order = new { amount = 250, currency = "TRY" }, noise = "must-not-land" });

        var instanceId = await WaitForInstanceByKeyAsync(orderId);
        await WaitUntilSettledAsync(Workflow, instanceId);
        Assert.Equal(("awaiting", "A"), await GetInstanceStateAsync(Workflow, instanceId));

        var instance = (await Api.GetInstanceAsync(Workflow, instanceId, Headers())).Body;
        Assert.Equal(orderId, instance.GetProperty("key").GetString());

        var data = instance.GetProperty("attributes");
        Assert.Equal(orderId, data.GetProperty("orderId").GetString());
        Assert.Equal("event", data.GetProperty("startedBy").GetString());
        Assert.Equal("evt-channel", data.GetProperty("channel").GetString());
        Assert.Equal(250, data.GetProperty("order").GetProperty("amount").GetInt32());
        Assert.Equal("TRY", data.GetProperty("order").GetProperty("currency").GetString());
        Assert.False(data.TryGetProperty("noise", out _), "only what the event input block wrote may land");
    }

    /// <summary>
    /// A transition event (<c>approve-by-event</c>, <c>triggerType: 3</c>) whose <c>input</c> block sets
    /// <c>$.key</c> correlates the ACTIVE instance by that key and runs the transition with the block's
    /// <c>attributes</c> as body: the instance moves to <c>approved</c> and the mapped fields land, the rest of the
    /// payload does not.
    /// </summary>
    [Fact]
    public async Task TransitionEvent_InputBlock_MovesInstance()
    {
        var orderId = NewKey("dml-evt");
        await PostEventAsync("start", null, new { orderId, order = new { amount = 90 } });
        var instanceId = await WaitForInstanceByKeyAsync(orderId);
        await WaitUntilSettledAsync(Workflow, instanceId);

        await PostEventAsync("transition", "approve-by-event",
            new { orderId, decision = "approved", approvedBy = "ops", noise = "must-not-land" });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Workflow, instanceId)).State == "approved",
            $"the approve event never moved {instanceId}", TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(Workflow, instanceId);
        Assert.Equal(("approved", "C"), await GetInstanceStateAsync(Workflow, instanceId));

        var data = await GetAttributesAsync(Workflow, instanceId);
        Assert.Equal("approved", data.GetProperty("decision").GetString());
        Assert.Equal("ops", data.GetProperty("approvedBy").GetString());
        Assert.Equal("event-input", data.GetProperty("approvedVia").GetString());
        Assert.False(data.TryGetProperty("noise", out _), "the payload outside the block must not land");
        Assert.Equal(90, data.GetProperty("order").GetProperty("amount").GetInt32()); // start data kept
    }

    /// <summary>
    /// The workflow <c>output</c> block shapes a SYNC start response: body = the block's <c>data</c>
    /// (<c>data.order</c> selected, <c>input.order.amount</c> from the admitted start attributes,
    /// <c>instance.currentState</c>), status <c>201</c> and header <c>x-dml-state</c> — instead of the standard
    /// envelope. The instance itself keeps its normal data.
    /// </summary>
    [Fact]
    public async Task SyncStart_WorkflowOutputBlock_ShapesResponse()
    {
        var key = NewKey("dml-out");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/v1/{TestDomain.Name}/workflows/{Workflow}/instances/start?sync=true")
        {
            Content = JsonContent.Create(new { key, attributes = new { order = new { amount = 410, currency = "EUR" } } })
        };
        foreach (var (name, value) in Headers()) request.Headers.TryAddWithoutValidation(name, value);

        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"expected 201 from the output block, got {(int)response.StatusCode}: {text}");
        Assert.True(response.Headers.TryGetValues("x-dml-state", out var state), $"x-dml-state header missing: {text}");
        Assert.Equal("awaiting", state!.Single());

        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        Assert.Equal(410, root.GetProperty("order").GetProperty("amount").GetInt32());
        Assert.Equal("EUR", root.GetProperty("order").GetProperty("currency").GetString());
        Assert.Equal("workflow-output", root.GetProperty("via").GetString());
        Assert.Equal(410, root.GetProperty("inputAmount").GetInt32());
        Assert.Equal("awaiting", root.GetProperty("state").GetString());
        Assert.False(root.TryGetProperty("id", out _), "the block replaces the standard envelope");

        // The response was shaped, the instance was not: it exists with its normal data under the key.
        var instanceId = await WaitForInstanceByKeyAsync(key);
        var data = await GetAttributesAsync(Workflow, instanceId);
        Assert.Equal(410, data.GetProperty("order").GetProperty("amount").GetInt32());
        Assert.False(data.TryGetProperty("via", out _), "the output block never writes instance data");
    }

    /// <summary>
    /// A function <c>output</c> block with <c>rawResponse: false</c>: the response is
    /// <c>{ dmlFnWrapped: data }</c>, built from both task slots (<c>result.first</c> / <c>result.second</c>) and the
    /// request body (<c>input</c>).
    /// </summary>
    [SkippableFact]
    public async Task Function_OutputBlock_Wrapped()
    {
        await SkipUnlessMockLabAsync();
        var customerId = NewKey("c");

        using var response = await _http.PostAsJsonAsync($"api/v1/{TestDomain.Name}/functions/dml-fn-wrapped", new { customerId });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"dml-fn-wrapped answered {(int)response.StatusCode}: {text}");

        using var body = JsonDocument.Parse(text);
        var data = body.RootElement.GetProperty("dmlFnWrapped");
        var first = data.GetProperty("first");
        Assert.Equal(customerId, first.GetProperty("customerId").GetString());
        Assert.Equal("fn-first", first.GetProperty("channel").GetString());
        Assert.Equal("static", first.GetProperty("source").GetString());
        Assert.Equal("second-" + customerId, data.GetProperty("secondCustomer").GetString());
        Assert.Equal(174, data.GetProperty("scoreSum").GetInt32());
        Assert.Equal(200, data.GetProperty("firstStatus").GetInt32());
        Assert.Equal(customerId, data.GetProperty("requested").GetString());
    }

    /// <summary>
    /// A function <c>output</c> block with <c>rawResponse: true</c>: <c>data</c> is the body as is,
    /// <c>$.statusCode</c> (<c>202</c>) and <c>$.headers.x-dml-score</c> (<c>result.first.body.score + 1</c>) reach the
    /// HTTP response.
    /// </summary>
    [SkippableFact]
    public async Task Function_OutputBlock_Raw()
    {
        await SkipUnlessMockLabAsync();
        var customerId = NewKey("c");

        using var response = await _http.PostAsJsonAsync($"api/v1/{TestDomain.Name}/functions/dml-fn-raw", new { customerId });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"expected 202 from the output block, got {(int)response.StatusCode}: {text}");
        Assert.True(response.Headers.TryGetValues("x-dml-score", out var score), $"x-dml-score header missing: {text}");
        Assert.Equal("88", score!.Single());

        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        Assert.False(root.TryGetProperty("dmlFnRaw", out _), "a raw response is not wrapped under the function key");
        Assert.Equal("second-" + customerId, root.GetProperty("echo").GetProperty("customerId").GetString());
        Assert.Equal("fn-second", root.GetProperty("echo").GetProperty("channel").GetString());
        Assert.Equal("fn-first", root.GetProperty("firstChannel").GetString());
        Assert.Equal("gold", root.GetProperty("level").GetString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string NewKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>POSTs an event straight to the delivery endpoint; the runtime must answer <c>SUCCESS</c>.</summary>
    private async Task PostEventAsync(string action, string? transitionKey, object payload)
    {
        var url = $"api/v1/{TestDomain.Name}/workflows/{Workflow}/instances/events?action={action}" +
                  (transitionKey is null ? "" : $"&transitionKey={transitionKey}");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("x-channel", "evt-channel");

        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"event {action} was refused with {(int)response.StatusCode}: {text}");
        Assert.Contains("SUCCESS", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Finds an instance by business key (the event answer carries no instance id).</summary>
    private async Task<string> WaitForInstanceByKeyAsync(string key)
    {
        string? instanceId = null;
        await WaitUntilAsync(async () =>
        {
            var response = await Api.ListInstancesAsync(Workflow, new Dictionary<string, string>
            {
                ["filter"] = $"{{\"key\":{{\"eq\":\"{key}\"}}}}"
            });
            if (!response.IsSuccessStatusCode
                || !response.Body.TryGetProperty("items", out var items)
                || items.GetArrayLength() == 0)
            {
                return false;
            }

            instanceId = items[0].GetProperty("id").GetString();
            return instanceId is not null;
        }, $"no {Workflow} instance with key {key}", TimeSpan.FromSeconds(60));
        return instanceId!;
    }

    private static async Task SkipUnlessMockLabAsync()
    {
        using var mocklab = new MockLabAdminClient();
        Skip.If(!await mocklab.IsUpAsync(),
            $"MockLab is not reachable at {mocklab.BaseUrl} — start it with `docker compose up -d` in the repo root.");
    }
}
