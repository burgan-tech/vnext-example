using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Core.IntegrationTests.Infrastructure;

/// <summary>
/// The instance ids of one built <c>chain-busy</c> chain: root (A) → middle (B) → leaf (C).
/// </summary>
public sealed record ChainBusyChain(string RootId, string MiddleId, string LeafId);

/// <summary>
/// Shared plumbing for the <c>chain-busy</c> behaviour tests.
/// <para>
/// These tests are the platform's behavioural control point: the three <c>chain-busy</c>
/// workflows are built so that pipeline behaviour is visible in instance data. Every onEntry /
/// onExit / onExecute hook increments a counter, and <c>leaf-waiting</c> arms a 30-minute
/// scheduled transition that never fires — its only job is to leave an armed timer whose
/// <c>executeAtUtc</c> proves whether a <c>$self</c> transition re-armed it.
/// </para>
/// <para>
/// Everything is asserted through the public API: <c>GET /instances/{id}</c> returns the
/// counters under <c>attributes</c>, and the state function returns the flattened active
/// correlation chain plus the armed scheduled entries. No database access is needed.
/// </para>
/// </summary>
public abstract class ChainBusyTestBase : IntegrationTestBase
{
    protected const string RootWorkflow = "chain-busy-root";
    protected const string MiddleWorkflow = "chain-busy-middle";
    protected const string LeafWorkflow = "chain-busy-leaf";

    protected const string LeafRestingState = "leaf-waiting";
    protected const string ScheduledTransitionName = "leaf-expire";

    /// <summary>Instance statuses that mean the instance will not move again on its own.</summary>
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal) { "C", "F", "P" };

    private readonly HttpClient _raw;

    protected ChainBusyTestBase(VNextTestEnvironment environment) : base(environment)
    {
        // The SDK client hard-codes ?sync=true on transitions. The accept-time behaviour under
        // test only exists on the async path, so those calls go out over a plain client.
        _raw = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
        _raw.DefaultRequestHeaders.Add("user_reference", "11111111-1111-1111-1111-111111111111");
    }

    // ── requests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a transition asynchronously (<c>sync=false</c>): the runtime accepts the request,
    /// answers 202 and executes it in a background job.
    /// </summary>
    protected async Task<(HttpStatusCode Status, JsonElement Body)> RunTransitionAsyncModeAsync(
        string workflow, string instanceId, string transitionKey, object? body = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}" +
                  $"/transitions/{transitionKey}?sync=false";

        using var response = await _raw.PatchAsJsonAsync(url, body ?? new { });
        var raw = await response.Content.ReadAsStringAsync();
        var parsed = string.IsNullOrWhiteSpace(raw)
            ? default
            : JsonDocument.Parse(raw).RootElement.Clone();

        return (response.StatusCode, parsed);
    }

    // ── reads ────────────────────────────────────────────────────────────────

    /// <summary>Instance attributes — where every behaviour counter lands.</summary>
    protected async Task<JsonElement> GetAttributesAsync(string workflow, string instanceId)
    {
        var response = await Api.GetInstanceAsync(workflow, instanceId);
        return response.Body.GetProperty("attributes");
    }

    /// <summary>Reads one counter; a counter that was never written reads as 0.</summary>
    protected async Task<int> GetCounterAsync(string workflow, string instanceId, string name)
    {
        var attributes = await GetAttributesAsync(workflow, instanceId);
        return attributes.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    /// <summary>The instance's own state and status, straight from the instance resource.</summary>
    protected async Task<(string State, string Status)> GetInstanceStateAsync(string workflow, string instanceId)
    {
        var response = await Api.GetInstanceAsync(workflow, instanceId);
        var metadata = response.Body.GetProperty("metadata");
        return (metadata.GetProperty("currentState").GetString() ?? "",
                metadata.GetProperty("status").GetString() ?? "");
    }

    /// <summary>
    /// A one-line description of where an instance actually got to, for failure messages. A counter
    /// assertion that only reports "expected 2, got 1" cannot distinguish a pipeline that skipped
    /// the step from a task that was silently bypassed; the instance's state and data usually can.
    /// </summary>
    protected async Task<string> DescribeAsync(string workflow, string instanceId)
    {
        try
        {
            var response = await Api.GetInstanceAsync(workflow, instanceId);
            var metadata = response.Body.GetProperty("metadata");
            var description = $"{workflow}/{instanceId[..8]} " +
                              $"{metadata.GetProperty("currentState").GetString()}/" +
                              $"{metadata.GetProperty("status").GetString()}";

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

    /// <summary>
    /// What a long-polling client sees: the state function walks the active-correlation chain
    /// and reports the DEEPEST active subflow's state and status.
    /// </summary>
    protected async Task<(string State, string Status)> GetObservedStateAsync(string workflow, string instanceId)
    {
        var response = await Api.CallInstanceFunctionAsync(workflow, instanceId, "state");
        return (response.Body.GetProperty("state").GetString() ?? "",
                response.Body.GetProperty("status").GetString() ?? "");
    }

    /// <summary>
    /// The armed scheduled transition's <c>executeAtUtc</c>, or null when no timer is armed.
    /// A changed value means the timer was re-armed.
    /// </summary>
    protected async Task<string?> GetScheduledExecuteAtAsync(string workflow, string instanceId, string name)
    {
        var response = await Api.CallInstanceFunctionAsync(workflow, instanceId, "state");
        if (!response.Body.TryGetProperty("transitions", out var transitions))
            return null;

        foreach (var transition in transitions.EnumerateArray())
        {
            if (transition.TryGetProperty("kind", out var kind) && kind.GetString() == "scheduled" &&
                transition.TryGetProperty("name", out var transitionName) && transitionName.GetString() == name)
            {
                return transition.TryGetProperty("executeAtUtc", out var executeAt)
                    ? executeAt.GetString()
                    : null;
            }
        }

        return null;
    }

    // ── chain construction ───────────────────────────────────────────────────

    /// <summary>
    /// Starts a root instance and drives it until the chain rests with the leaf waiting for
    /// input. The chain is built entirely by auto transitions, so no manual step is needed.
    /// </summary>
    protected async Task<ChainBusyChain> BuildChainAsync(string tag)
    {
        var start = await Api.StartInstanceAsync(RootWorkflow, new { testId = $"{tag}-{Guid.NewGuid():N}"[..24] });
        var rootId = start.Body.GetProperty("id").GetString()
                     ?? throw new InvalidOperationException("start response carried no instance id");

        await WaitUntilAsync(
            async () => (await GetObservedStateAsync(RootWorkflow, rootId)).State == LeafRestingState,
            $"chain did not reach '{LeafRestingState}'",
            TimeSpan.FromSeconds(60));

        var (middleId, leafId) = await ResolveChainAsync(rootId);
        return new ChainBusyChain(rootId, middleId, leafId);
    }

    /// <summary>
    /// Resolves the whole chain from the root's state function: <c>activeCorrelations</c> is
    /// flattened across levels, so one call yields both descendants.
    /// </summary>
    private async Task<(string MiddleId, string LeafId)> ResolveChainAsync(string rootId)
    {
        var response = await Api.CallInstanceFunctionAsync(RootWorkflow, rootId, "state");
        var correlations = response.Body.GetProperty("activeCorrelations");

        string? middleId = null, leafId = null;
        foreach (var correlation in correlations.EnumerateArray())
        {
            var name = correlation.GetProperty("subFlowName").GetString();
            var id = correlation.GetProperty("subFlowInstanceId").GetString();
            if (name == MiddleWorkflow) middleId = id;
            else if (name == LeafWorkflow) leafId = id;
        }

        Assert.False(middleId is null || leafId is null,
            $"could not resolve the chain from the root's active correlations (middle={middleId}, leaf={leafId})");

        return (middleId!, leafId!);
    }

    // ── waiting ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, then returns. The SDK ships no waiting
    /// helper, and every assertion here is about a state the runtime reaches asynchronously.
    /// </summary>
    protected static async Task WaitUntilAsync(
        Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(30);
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < budget)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }

        Assert.Fail($"Timed out after {budget.TotalSeconds:0}s: {because}");
    }

    /// <summary>Waits for an instance to stop being Busy — i.e. its transition finished.</summary>
    protected Task WaitUntilSettledAsync(string workflow, string instanceId) =>
        WaitUntilAsync(
            async () => (await GetInstanceStateAsync(workflow, instanceId)).Status != "B",
            $"{workflow}/{instanceId} stayed Busy");

    /// <summary>Waits for an instance to reach a terminal status.</summary>
    protected Task WaitUntilTerminalAsync(string workflow, string instanceId, TimeSpan? timeout = null) =>
        WaitUntilAsync(
            async () => TerminalStatuses.Contains((await GetInstanceStateAsync(workflow, instanceId)).Status),
            $"{workflow}/{instanceId} did not reach a terminal status",
            timeout);

    protected static bool IsTerminal(string status) => TerminalStatuses.Contains(status);
}
