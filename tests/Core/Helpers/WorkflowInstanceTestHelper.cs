using System.Globalization;
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

    /// <summary>
    /// Returns the <c>attributes</c> object from <c>GET .../instances/{id}</c>.
    /// Fails the test when the response does not include an <c>attributes</c> property.
    /// </summary>
    public async Task<JsonElement> GetAttributesAsync(string instanceId)
    {
        var response = await _api.GetInstanceAsync(_workflowKey, instanceId);
        Assert.True(
            response.Body.TryGetProperty("attributes", out var attributes),
            "GetInstance response should include 'attributes'."
        );
        return attributes;
    }

    /// <summary>
    /// Returns the full response body from <c>GET .../instances/{id}</c>.
    /// </summary>
    public async Task<JsonElement> GetInstanceBodyAsync(string instanceId)
    {
        var response = await _api.GetInstanceAsync(_workflowKey, instanceId);
        return response.Body;
    }

    /// <summary>
    /// Returns the full response body from <c>GET .../instances/{id}?{query}</c> via raw path.
    /// Useful for adding query parameters like <c>?extensions=...</c>.
    /// </summary>
    public async Task<JsonElement> GetInstanceRawAsync(
        string instanceId,
        Dictionary<string, string>? queryParams = null
    )
    {
        var qs = queryParams != null
            ? string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))
            : "";
        var path = $"/api/v{_apiVersion}/{_domain}/workflows/{Uri.EscapeDataString(_workflowKey)}/instances/{Uri.EscapeDataString(instanceId)}";
        if (!string.IsNullOrEmpty(qs))
            path += "?" + qs;

        var response = await _api.GetRawAsync(path);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return response.Body;
    }

    /// <summary>
    /// Calls a named instance function (<c>GET .../functions/{functionName}</c>) and returns the response body.
    /// Useful for <c>view</c>, <c>data</c>, custom functions, etc.
    /// </summary>
    public async Task<JsonElement> CallFunctionAsync(
        string instanceId,
        string functionName,
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, string>? headers = null
    )
    {
        var response = await _api.CallInstanceFunctionAsync(
            _workflowKey,
            instanceId,
            functionName,
            queryParams,
            headers
        );
        return response.Body;
    }

    /// <summary>
    /// Calls <c>GET .../instances</c> with any subset of <c>filter</c> / <c>sort</c> / <c>page</c> /
    /// <c>pageSize</c> query parameters and asserts HTTP 200 before returning the response body.
    /// <paramref name="filterJson"/> is the GraphQL / JSON filter string expected by the runtime
    /// (see <c>vnext-runtime</c> instance-filtering doc; e.g. <c>{"attributes":{"category":{"eq":"finance"}}}</c>).
    /// </summary>
    public async Task<JsonElement> ListInstancesAsync(
        string? filterJson = null,
        string? sort = null,
        int? page = null,
        int? pageSize = null,
        Dictionary<string, string>? extraQuery = null
    )
    {
        var qp = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(filterJson))
            qp["filter"] = filterJson;
        if (!string.IsNullOrEmpty(sort))
            qp["sort"] = sort;
        if (page.HasValue)
            qp["page"] = page.Value.ToString(CultureInfo.InvariantCulture);
        if (pageSize.HasValue)
            qp["pageSize"] = pageSize.Value.ToString(CultureInfo.InvariantCulture);

        if (extraQuery is not null)
        {
            foreach (var kv in extraQuery)
                qp[kv.Key] = kv.Value;
        }

        var response = await _api.ListInstancesAsync(_workflowKey, qp);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Body;
    }
}
