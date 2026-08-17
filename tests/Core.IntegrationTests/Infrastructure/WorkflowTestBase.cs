using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Core.IntegrationTests.Infrastructure;

/// <summary>
/// Shared plumbing for domain-workflow integration tests.
/// <para>
/// Two things the SDK client does not give us and every one of these tests needs: a transition
/// that runs asynchronously (the client hard-codes <c>?sync=true</c>) and a way to wait for a
/// state the runtime reaches in the background. Both live here.
/// </para>
/// <para>
/// Several domain workflows are role-gated — without a caller role their state function answers
/// 403 and the flow looks broken for a reason that has nothing to do with the behaviour under
/// test. Pass the roles the workflow declares via <see cref="Headers"/>.
/// </para>
/// </summary>
public abstract class WorkflowTestBase : IntegrationTestBase
{
    private const string CallerUser = "11111111-1111-1111-1111-111111111111";

    /// <summary>Statuses that mean the instance will not move again on its own.</summary>
    protected static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal) { "C", "F", "P" };

    private readonly HttpClient _raw;

    protected WorkflowTestBase(VNextTestEnvironment environment) : base(environment)
    {
        _raw = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    /// <summary>
    /// Caller headers. <c>roles</c> feeds both header spellings the runtime accepts, so a test
    /// does not have to know which one this deployment resolves from.
    /// </summary>
    protected static Dictionary<string, string> Headers(string? roles = null)
    {
        var headers = new Dictionary<string, string>
        {
            ["user_reference"] = CallerUser,
            ["x-request-id"] = Guid.NewGuid().ToString(),
            ["x-device-id"] = "integration-test-device",
            ["x-token-id"] = "integration-test-token",
        };

        if (!string.IsNullOrEmpty(roles))
        {
            headers["x-roles"] = roles;
            headers["role"] = roles;
        }

        return headers;
    }

    // ── requests ─────────────────────────────────────────────────────────────

    /// <summary>Starts an instance and returns its id.</summary>
    protected async Task<string> StartAsync(string workflow, object body, string? roles = null)
    {
        var response = await Api.StartInstanceAsync(workflow, body, Headers(roles));
        return response.Body.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("start response carried no instance id");
    }

    /// <summary>
    /// Runs a transition asynchronously: the runtime accepts it, answers 202 and executes it in
    /// a background job. Returns the status so a test can assert on rejections too.
    /// </summary>
    protected async Task<HttpStatusCode> RunAsync(
        string workflow, string instanceId, string transitionKey, object? body = null, string? roles = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}" +
                  $"/transitions/{transitionKey}?sync=false";

        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body ?? new { })
        };
        foreach (var (key, value) in Headers(roles)) request.Headers.TryAddWithoutValidation(key, value);

        using var response = await _raw.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// An unmediated request against the orchestrator, for tests that must control the headers
    /// themselves — a caller with no role, a request with no <c>x-device-id</c>. Both the SDK
    /// client and <see cref="Headers"/> always send a complete set, which is exactly what those
    /// tests must not do.
    /// </summary>
    protected async Task<(HttpStatusCode Status, string Body)> SendRawAsync(
        HttpMethod method, string url, object? body = null, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, url.TrimStart('/'));
        if (body is not null) request.Content = JsonContent.Create(body);
        if (headers is not null)
        {
            foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await _raw.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Runs a transition and waits for the instance to stop being Busy.</summary>
    protected async Task<HttpStatusCode> RunAndSettleAsync(
        string workflow, string instanceId, string transitionKey, object? body = null, string? roles = null)
    {
        var status = await RunAsync(workflow, instanceId, transitionKey, body, roles);
        if ((int)status < 400) await WaitUntilSettledAsync(workflow, instanceId, roles);
        return status;
    }

    /// <summary>
    /// Runs a transition that the test requires to be ACCEPTED, and waits for it to settle.
    /// <para>
    /// Prefer this over <see cref="RunAndSettleAsync"/> whenever a refusal would be a test
    /// failure. <c>RunAndSettleAsync</c> returns the status and a caller that ignores it turns a
    /// rejected request into a downstream "never reached state X" timeout — 45 wasted seconds and
    /// a message that names the symptom instead of the cause. This one fails immediately and
    /// quotes the runtime's own error body.
    /// </para>
    /// </summary>
    protected async Task RunAcceptedAsync(
        string workflow, string instanceId, string transitionKey, object? body = null, string? roles = null,
        TimeSpan? settleTimeout = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}" +
                  $"/transitions/{transitionKey}?sync=false";

        var (status, responseBody) = await SendRawAsync(
            HttpMethod.Patch, url, body ?? new { }, Headers(roles));

        Assert.True((int)status < 400,
            $"'{transitionKey}' was refused with {(int)status}: {responseBody}");

        await WaitUntilSettledAsync(workflow, instanceId, roles, settleTimeout);
    }

    // ── reads ────────────────────────────────────────────────────────────────

    /// <summary>The instance's own state and status.</summary>
    protected async Task<(string State, string Status)> GetInstanceStateAsync(
        string workflow, string instanceId, string? roles = null)
    {
        var response = await Api.GetInstanceAsync(workflow, instanceId, Headers(roles));
        var metadata = response.Body.GetProperty("metadata");
        return (metadata.GetProperty("currentState").GetString() ?? "",
                metadata.GetProperty("status").GetString() ?? "");
    }

    /// <summary>
    /// What a polling client sees. For a chain this is the DEEPEST active subflow, not the
    /// instance addressed.
    /// </summary>
    protected async Task<(string State, string Status)> GetObservedStateAsync(
        string workflow, string instanceId, string? roles = null)
    {
        var response = await Api.CallInstanceFunctionAsync(workflow, instanceId, "state", headers: Headers(roles));
        return (response.Body.GetProperty("state").GetString() ?? "",
                response.Body.GetProperty("status").GetString() ?? "");
    }

    /// <summary>Instance data (attributes) — where task output lands.</summary>
    protected async Task<JsonElement> GetAttributesAsync(string workflow, string instanceId, string? roles = null)
    {
        var response = await Api.GetInstanceAsync(workflow, instanceId, Headers(roles));
        return response.Body.GetProperty("attributes");
    }

    /// <summary>Reads an integer attribute; absent reads as 0.</summary>
    protected async Task<int> GetCounterAsync(string workflow, string instanceId, string name, string? roles = null)
    {
        var attributes = await GetAttributesAsync(workflow, instanceId, roles);
        return attributes.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
    }

    /// <summary>The armed scheduled transition's execution time, or null when none is armed.</summary>
    protected async Task<string?> GetScheduledExecuteAtAsync(
        string workflow, string instanceId, string name, string? roles = null)
    {
        var response = await Api.CallInstanceFunctionAsync(workflow, instanceId, "state", headers: Headers(roles));
        if (!response.Body.TryGetProperty("transitions", out var transitions)) return null;

        foreach (var transition in transitions.EnumerateArray())
        {
            if (transition.TryGetProperty("kind", out var kind) && kind.GetString() == "scheduled" &&
                transition.TryGetProperty("name", out var transitionName) && transitionName.GetString() == name)
            {
                return transition.TryGetProperty("executeAtUtc", out var at) ? at.GetString() : null;
            }
        }

        return null;
    }

    /// <summary>Open subflow correlations, flattened across the whole chain.</summary>
    protected async Task<Dictionary<string, string>> GetActiveSubflowsAsync(
        string workflow, string instanceId, string? roles = null)
    {
        var response = await Api.CallInstanceFunctionAsync(workflow, instanceId, "state", headers: Headers(roles));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!response.Body.TryGetProperty("activeCorrelations", out var correlations)) return result;

        foreach (var correlation in correlations.EnumerateArray())
        {
            var name = correlation.GetProperty("subFlowName").GetString();
            var id = correlation.GetProperty("subFlowInstanceId").GetString();
            if (name is not null && id is not null) result[name] = id;
        }

        return result;
    }

    // ── waiting ──────────────────────────────────────────────────────────────

    protected static async Task WaitUntilAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(45);
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < budget)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }

        Assert.Fail($"Timed out after {budget.TotalSeconds:0}s: {because}");
    }

    /// <summary>
    /// A one-line description of where an instance actually got to, for failure messages. A
    /// suite that only says "timed out" makes every environment problem look the same; a faulted
    /// instance names the task that failed, and that is usually the whole answer.
    /// </summary>
    protected async Task<string> DescribeAsync(string workflow, string instanceId, string? roles = null)
    {
        try
        {
            var response = await Api.GetInstanceAsync(workflow, instanceId, Headers(roles));
            var metadata = response.Body.GetProperty("metadata");
            var state = metadata.GetProperty("currentState").GetString();
            var status = metadata.GetProperty("status").GetString();

            var description = $"{workflow}/{instanceId[..8]} {state}/{status}";

            if (response.Body.TryGetProperty("attributes", out var attributes))
            {
                var raw = attributes.GetRawText();
                description += $" attrs={(raw.Length > 400 ? raw[..400] + "…" : raw)}";
            }

            return description;
        }
        catch (Exception exception)
        {
            return $"{workflow}/{instanceId} — could not be read: {exception.Message}";
        }
    }

    protected Task WaitUntilSettledAsync(
        string workflow, string instanceId, string? roles = null, TimeSpan? timeout = null) =>
        WaitUntilAsync(
            async () => (await GetInstanceStateAsync(workflow, instanceId, roles)).Status != "B",
            $"{workflow}/{instanceId} stayed Busy",
            timeout);

    /// <summary>
    /// Fails the test as soon as an instance faults, rather than letting the caller burn its whole
    /// timeout waiting for a state the instance can no longer reach. A faulted instance names the
    /// step that broke, which is almost always the entire answer; "never reached state X" names
    /// only the symptom, and every unrelated environment problem produces the same message.
    /// </summary>
    private async Task ThrowIfFaultedAsync(string workflow, string instanceId, string? roles)
    {
        if ((await GetInstanceStateAsync(workflow, instanceId, roles)).Status != "F") return;

        Assert.Fail($"{workflow}/{instanceId[..8]} FAULTED while being waited on — " +
                    await DescribeAsync(workflow, instanceId, roles));
    }

    /// <summary>Waits for the observed (deepest) state to match.</summary>
    protected Task WaitForObservedStateAsync(
        string workflow, string instanceId, string state, string? roles = null, TimeSpan? timeout = null) =>
        WaitUntilAsync(
            async () => (await GetObservedStateAsync(workflow, instanceId, roles)).State == state,
            $"{workflow}/{instanceId} never reached observed state '{state}'",
            timeout);

    /// <summary>
    /// Waits for a state, aborting early if the instance faults on the way. Use for any state the
    /// flow is expected to reach under its own power.
    /// </summary>
    protected Task WaitForInstanceStateAsync(
        string workflow, string instanceId, string state, string? roles = null, TimeSpan? timeout = null) =>
        WaitUntilAsync(
            async () =>
            {
                var (current, _) = await GetInstanceStateAsync(workflow, instanceId, roles);
                if (current == state) return true;
                await ThrowIfFaultedAsync(workflow, instanceId, roles);
                return false;
            },
            $"{workflow}/{instanceId} never reached state '{state}'",
            timeout);

    /// <summary>
    /// Asserts an instance is healthy — present, not faulted. Worth calling right after a start
    /// whose initial state runs entry tasks: the instance reaches the state either way, so a
    /// state-only wait reports success on an instance that has already broken.
    /// </summary>
    protected async Task AssertNotFaultedAsync(string workflow, string instanceId, string? roles = null)
    {
        var (_, status) = await GetInstanceStateAsync(workflow, instanceId, roles);
        Assert.True(status != "F", $"instance faulted — {await DescribeAsync(workflow, instanceId, roles)}");
    }
}
