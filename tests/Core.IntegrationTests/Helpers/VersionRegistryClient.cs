using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Core.IntegrationTests.Helpers;

/// <summary>
/// The <c>sys-version-registry</c> pointer of a flow (flow default version, vnext #1106): instance key = flow key,
/// data <c>{ type: "Flow", defaultVersion }</c>. Shared by <c>FlowDefaultVersionTests</c> and the row-security lab
/// (whose rule set follows the pointer, K2). A pointer change reaches each pod within ~5 s; callers wait for the
/// effect themselves.
/// <para>
/// The registry flow may not be on the runtime when the core system package predates it, so a verbatim copy lives
/// under <c>Tests/FlowDefaultVersion/fixtures/</c> and <see cref="EnsureRegistryAsync"/> publishes it once per run,
/// schema first ("already published" is fine).
/// </para>
/// </summary>
public sealed class VersionRegistryClient
{
    /// <summary>The registry flow key.</summary>
    public const string Workflow = "sys-version-registry";

    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private static bool _registryPublished;

    private readonly HttpClient _http;
    private readonly Func<IDictionary<string, string>> _headers;

    /// <param name="orchestratorBaseUrl">The core orchestrator base URL.</param>
    /// <param name="headers">Caller headers for every registry request (the suite's standard set).</param>
    public VersionRegistryClient(string orchestratorBaseUrl, Func<IDictionary<string, string>> headers)
    {
        _http = new HttpClient { BaseAddress = new Uri(orchestratorBaseUrl.TrimEnd('/') + "/") };
        _headers = headers;
    }

    /// <summary>The <c>set-default</c> / start body for a pointer value.</summary>
    public static object Body(string? pointer) =>
        new { attributes = new { type = "Flow", defaultVersion = pointer } };

    /// <summary>Starts (or finds) the registry entry of a flow and returns its instance id.</summary>
    public async Task<string> RegistryIdAsync(string flow)
    {
        var (status, body) = await SendAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{Workflow}/instances/start?sync=true",
            new { key = flow, attributes = new { type = "Flow", defaultVersion = (string?)null } });
        Assert.True((int)status < 400, $"registry start for {flow} refused with {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Points the flow at <paramref name="pointer"/>; creates the entry on first use, set-default after.</summary>
    public async Task SetPointerAsync(string flow, string? pointer)
    {
        var id = await RegistryIdAsync(flow);
        await WaitForActiveAsync(id);

        var (status, body) = await SendAsync(HttpMethod.Patch,
            $"api/v1/core/workflows/{Workflow}/instances/{id}/transitions/set-default?sync=true", Body(pointer));
        Assert.True((int)status < 400, $"set-default {flow} -> {pointer ?? "null"} refused with {(int)status}: {body}");
    }

    /// <summary>
    /// Sends <c>set-default</c> and returns the raw answer, for tests that expect a refusal (409 <c>Instance:100052</c>).
    /// </summary>
    public async Task<(HttpStatusCode Status, string Body)> TrySetPointerAsync(string flow, string? pointer)
    {
        var id = await RegistryIdAsync(flow);
        await WaitForActiveAsync(id);
        return await SendAsync(HttpMethod.Patch,
            $"api/v1/core/workflows/{Workflow}/instances/{id}/transitions/set-default?sync=true", Body(pointer));
    }

    /// <summary>
    /// Returns the flow to Automatic with <c>"latest"</c>: the runtime's instance-data merge ignores null on an
    /// existing key, so null could not clear. Never throws (cleanup must not mask the test result).
    /// </summary>
    public async Task ClearPointerAsync(string flow)
    {
        try { await SetPointerAsync(flow, "latest"); } catch (Exception) { /* cleanup must not mask the test result */ }
    }

    /// <summary>Publishes the registry schema, then its workflow, once per run.</summary>
    public async Task EnsureRegistryAsync()
    {
        await PublishGate.WaitAsync();
        try
        {
            if (_registryPublished) return;
            var dir = Path.Combine(RepoRoot(), "tests", "Core.IntegrationTests", "Tests", "FlowDefaultVersion", "fixtures");
            await PublishAsync(File.ReadAllText(Path.Combine(dir, "version-registry.schema.json")), "version-registry");
            await PublishAsync(File.ReadAllText(Path.Combine(dir, "sys-version-registry.workflow.json")), Workflow);
            _registryPublished = true;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    private async Task PublishAsync(string json, string what)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/definitions/publish")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        foreach (var (key, value) in _headers()) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var alreadyThere = response.StatusCode == HttpStatusCode.Conflict
                           || body.Contains("already", StringComparison.OrdinalIgnoreCase);
        Assert.True((int)response.StatusCode < 300 || alreadyThere,
            $"publishing {what} answered {(int)response.StatusCode}: {body}");
    }

    private async Task WaitForActiveAsync(string id)
    {
        var clock = Stopwatch.StartNew();
        string? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(45))
        {
            var (status, body) = await SendAsync(HttpMethod.Get, $"api/v1/core/workflows/{Workflow}/instances/{id}", null);
            if (status == HttpStatusCode.OK)
            {
                var metadata = JsonDocument.Parse(body).RootElement.GetProperty("metadata");
                last = metadata.GetProperty("currentState").GetString();
                if (last == "active") return;
                if (metadata.GetProperty("status").GetString() == "F")
                    Assert.Fail($"{Workflow}/{id} FAULTED while being waited on: {body}");
            }
            else
            {
                last = $"HTTP {(int)status}";
            }

            await Task.Delay(250);
        }

        Assert.Fail($"Timed out after 45s: {Workflow}/{id} never reached state 'active' (last {last})");
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        foreach (var (key, value) in _headers()) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await _http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "vnext.config.json"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("vnext.config.json not found above " + AppContext.BaseDirectory);
    }
}
