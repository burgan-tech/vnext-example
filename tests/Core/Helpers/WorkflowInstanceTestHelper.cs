using System.Net;
using System.Text.Json;
using VNext.Testing.Sdk.Client;
using Xunit;

namespace Core.IntegrationTests.Helpers;

/// <summary>
/// Reusable helpers for workflow instance API flows (start, state, transitions, authorize) against a single workflow key.
/// </summary>
public sealed class WorkflowInstanceTestHelper
{
    private readonly VNextApiClient _api;
    private readonly string _workflowKey;
    private readonly string _domain;
    private readonly string _apiVersion;

    public WorkflowInstanceTestHelper(
        VNextApiClient api,
        string workflowKey,
        string domain = "core",
        string apiVersion = "1"
    )
    {
        _api = api;
        _workflowKey = workflowKey;
        _domain = domain;
        _apiVersion = apiVersion;
    }

    /// <summary>URL-segment safe unique instance key.</summary>
    public static string UniqueInstanceKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    public async Task<string> StartInstanceIdAsync(
        object body,
        Dictionary<string, string>? headers = null
    )
    {
        var response = await _api.StartInstanceAsync(_workflowKey, body, headers);
        Assert.True(
            response.Body.TryGetProperty("id", out var idEl),
            "Start response should contain id"
        );
        var id = idEl.GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        return id!;
    }

    public Task RunTransitionAsync(
        string instanceId,
        string transitionKey,
        Dictionary<string, string>? headers,
        object? transitionBody = null
    ) =>
        _api.RunTransitionAsync(
            _workflowKey,
            instanceId,
            transitionKey,
            transitionBody ?? new { },
            headers
        );

    public async Task<string> GetStateNameAsync(string instanceId)
    {
        var response = await _api.CallInstanceFunctionAsync(
            _workflowKey,
            instanceId,
            "state",
            null,
            null
        );

        return StateFunctionJson.ExtractStateName(response.Body);
    }

    public async Task<JsonElement> GetStateFunctionBodyAsync(
        string instanceId,
        Dictionary<string, string>? headers
    )
    {
        var response = await _api.CallInstanceFunctionAsync(
            _workflowKey,
            instanceId,
            "state",
            null,
            headers
        );
        return response.Body;
    }

    public async Task AssertStateAsync(string instanceId, string expected)
    {
        var name = await GetStateNameAsync(instanceId);
        Assert.Equal(expected, name);
    }

    public async Task WaitForStateAsync(string instanceId, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await GetStateNameAsync(instanceId) == expected)
                return;
            await Task.Delay(200);
        }

        var current = await GetStateNameAsync(instanceId);
        Assert.Fail($"Timeout waiting for state '{expected}'. Current: '{current}'");
    }

    /// <summary>
    /// Polls state until one of <paramref name="stopOnAny"/> matches, or timeout; returns last observed state name.
    /// </summary>
    public async Task<string> PollStateUntilAnyAsync(
        string instanceId,
        TimeSpan window,
        IReadOnlySet<string> stopOnAny,
        int pollIntervalMs = 20
    )
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var s = await GetStateNameAsync(instanceId);
            if (stopOnAny.Contains(s))
                return s;
            await Task.Delay(pollIntervalMs);
        }

        return await GetStateNameAsync(instanceId);
    }

    public async Task AssertAuthorizeQueryRolesAsync(
        string instanceId,
        string role,
        bool expectAllowed
    )
    {
        var path =
            $"/api/v{_apiVersion}/{_domain}/workflows/{Uri.EscapeDataString(_workflowKey)}/instances/{Uri.EscapeDataString(instanceId)}/functions/authorize?queryRoles=true&role={Uri.EscapeDataString(role)}";

        var response = await _api.GetRawAsync(path);
        if (expectAllowed)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        else
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
