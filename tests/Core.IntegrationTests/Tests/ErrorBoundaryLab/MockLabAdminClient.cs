using System.Net.Http.Json;
using System.Text.Json;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// The slice of MockLab's admin API this lab needs.
/// <para>
/// Only one endpoint of the lab is stateful: <c>api/eb-lab/flaky</c> answers 500, 500, 200 from a
/// sequence whose cursor lives in MockLab's memory and is shared by every caller. A retry test that
/// does not rewind it first sees whatever position the previous run left behind, so the recovery it
/// claims to observe would be an accident. <see cref="ResetSequenceAsync"/> is what makes that test
/// a measurement instead of a coincidence.
/// </para>
/// <para>
/// MockLab is not part of the SDK's container stack and has no env var of its own; against a local
/// runtime it is the <c>docker compose up</c> service on <c>http://localhost:3001</c>. Override with
/// <c>MOCKLAB_BASE_URL</c>. When it is unreachable the MockLab-dependent tests skip rather than fail —
/// the rest of the lab runs on the script-based failure injector and needs nothing external.
/// </para>
/// </summary>
public sealed class MockLabAdminClient : IDisposable
{
    private readonly HttpClient _http;

    public MockLabAdminClient()
    {
        BaseUrl = (Environment.GetEnvironmentVariable("MOCKLAB_BASE_URL")?.Trim().TrimEnd('/')
                   is { Length: > 0 } configured)
            ? configured
            : "http://localhost:3001";

        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public string BaseUrl { get; }

    /// <summary>True when MockLab answers its admin listing — the precondition for the HTTP cases.</summary>
    public async Task<bool> IsUpAsync()
    {
        try
        {
            using var response = await _http.GetAsync("_admin/mocks");
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The mock id serving <paramref name="route"/>, or null when the seed is not loaded.</summary>
    public async Task<int?> FindMockIdAsync(string route)
    {
        using var response = await _http.GetAsync("_admin/mocks");
        if (!response.IsSuccessStatusCode) return null;

        var mocks = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var mock in mocks.EnumerateArray())
        {
            if (mock.TryGetProperty("route", out var value) &&
                string.Equals(value.GetString(), route, StringComparison.OrdinalIgnoreCase))
            {
                return mock.GetProperty("id").GetInt32();
            }
        }

        return null;
    }

    /// <summary>
    /// Rewinds a sequential mock to its first item. Returns false when the mock is unknown, which the
    /// caller should treat as "the seed is missing" and skip — not as a failure of the behaviour.
    /// </summary>
    public async Task<bool> ResetSequenceAsync(string route)
    {
        var id = await FindMockIdAsync(route);
        if (id is null) return false;

        using var response = await _http.PostAsync($"_admin/mocks/{id}/sequence/reset", content: null);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => _http.Dispose();
}
