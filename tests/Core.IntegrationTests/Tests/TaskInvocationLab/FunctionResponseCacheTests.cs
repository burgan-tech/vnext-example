using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.TaskInvocationLab;

/// <summary>
/// The fourth path issue #1007 moved onto the routing seam, and the one no other scenario in this
/// repository exercises: the FUNCTION RESPONSE CACHE. <c>FunctionAppService</c> reads and writes it
/// through <c>IStateStoreCacheGateway</c>, which since #1007 goes out through
/// <c>ITaskInvocationDispatcher</c> — the same seam as the five wire task types, so it runs
/// in-process on Orchestration by default and falls back to the Execution service when
/// <c>Workflow:TaskInvocation:Modes:statestore</c> is set to <c>Remote</c>.
/// <para>
/// <b>Why a dedicated function exists for this.</b> Before <c>til-cached-echo</c>, no component in
/// this repository configured <c>function.cache</c> at all, so this gateway — and the
/// <c>Cache.Get</c>/<c>Cache.Set</c> spans it emits under component type <c>function-response</c> —
/// was never reached end to end in any run, local or CI.
/// </para>
/// </summary>
/// <remarks>
/// Like <see cref="ResultModelParityTests"/>, this file is meant to be run in BOTH routing modes
/// and to pass unchanged in each: nothing here branches on the active mode. The measured cost does
/// differ (a cache hit resolves in roughly 0.7 ms in-process against roughly 2.2 ms through the
/// Execution service), but cost is not what this file asserts — behaviour is.
/// </remarks>
public sealed class FunctionResponseCacheTests : TaskInvocationLabTestBase
{
    private const string FunctionKey = "til-cached-echo";

    public FunctionResponseCacheTests(VNextTestEnvironment environment) : base(environment)
    {
    }

    /// <summary>
    /// A miss computes the response and a hit replays it verbatim. The proof is
    /// <c>computedAtUtc</c>, which the mapping stamps with <c>DateTime.UtcNow</c> on every
    /// execution: if the second call returns the SAME stamp, the function's task set did not run
    /// and the value came out of the cache. Asserting on timing instead would be a flake.
    /// </summary>
    [SkippableFact]
    public async Task CachedFunction_ServesTheSecondCallFromTheCache_WithoutReExecutingItsTasks()
    {
        Skip.If(!await IsMockLabUpAsync(), $"MockLab is not reachable at {MockLabBaseUrl()}.");

        // The cached response outlives a single test (ttlInSeconds = 60 on the component), so the
        // first call here may well be a hit left by an earlier run. Take three readings: whatever
        // the first one was, calls two and three must agree with each other.
        var first = await ReadStampAsync();
        var second = await ReadStampAsync();
        var third = await ReadStampAsync();

        Assert.False(string.IsNullOrWhiteSpace(second), "the cached function returned no computedAtUtc");
        Assert.Equal(second, third);
        Assert.Equal(first, second);
    }

    /// <summary>
    /// The cache must not change what the caller sees. A cached response is the same
    /// <c>FunctionResponseOutput</c> the miss produced — same wrapper key, same payload fields —
    /// because it is deserialized back into that type rather than replayed as raw bytes.
    /// </summary>
    [SkippableFact]
    public async Task CachedFunction_KeepsTheResponseShape_OnBothTheMissAndTheHit()
    {
        Skip.If(!await IsMockLabUpAsync(), $"MockLab is not reachable at {MockLabBaseUrl()}.");

        for (var call = 0; call < 2; call++)
        {
            using var response = await RawClient.GetAsync($"api/v1/core/functions/{FunctionKey}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("tilCachedEcho").GetProperty("data");

            Assert.Equal("til-cached-echo", data.GetProperty("source").GetString());
            Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("computedAtUtc").GetString()));
        }
    }

    private async Task<string?> ReadStampAsync()
    {
        using var response = await RawClient.GetAsync($"api/v1/core/functions/{FunctionKey}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement
            .GetProperty("tilCachedEcho")
            .GetProperty("data")
            .GetProperty("computedAtUtc")
            .GetString();
    }
}
