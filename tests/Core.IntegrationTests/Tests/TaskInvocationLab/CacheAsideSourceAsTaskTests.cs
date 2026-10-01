using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.TaskInvocationLab;

/// <summary>
/// CacheAside (type 18) after vnext #1048: the source runs AS A TASK, with the CacheAside config's
/// <c>sourceMapping</c> as its <c>IMapping</c> (InputHandler before the call on a miss, OutputHandler
/// after it; the OutputHandler's Data is what gets cached), and <c>key</c> is a string or a
/// <c>ScriptCode</c> (Dynamic Expresso or C# <c>ICacheKeyMapping</c>, each as NAT, B64 or REF).
/// <para>
/// <b>Every key here is per instance</b> (<c>til:ca:{instanceKey}:{variant}</c>, the instance key a
/// fresh GUID this class chooses), so a cache HIT can only have been produced by an earlier run on the
/// SAME instance — unlike <see cref="TaskTypeInvocationTests.CacheAsideRoundTrip_MissesThenHits"/>,
/// whose static key any other test may have warmed. The same-key re-run is the same case transition
/// fired twice: every <c>case-cacheaside-getdata</c> / <c>case-cacheaside-key-*</c> transition targets
/// <c>$self</c>, so the instance stays on <c>ready</c> and can run it again.
/// </para>
/// <para>
/// Assertions read the <c>til*</c> projection <c>TilResultProjection.csx</c> writes as the
/// TRANSITION-level mapping, which runs on the CacheAside result for hits and misses alike:
/// <c>tilMetadata</c> (camelCased on read-back: <c>cacheHit</c>, <c>refreshed</c>, <c>key</c>, …) and
/// <c>tilData</c> — the cached value, i.e. <c>TilCacheSourceMapping</c>'s
/// <c>{ shaped, computedAtUtc, payload }</c>. <c>computedAtUtc</c> is stamped once, at source time, so
/// an unchanged stamp on the re-run proves the source did not run again independently of the flag.
/// </para>
/// </summary>
public class CacheAsideSourceAsTaskTests : TaskInvocationLabTestBase
{
    public CacheAsideSourceAsTaskTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// The HTTP source (<c>til-cache-source</c>, URL <c>API_BASEURL/api/til/cache-source</c>) only works
    /// if the source mapping's InputHandler ran and resolved the placeholder. First run: miss, source
    /// called, shaped value cached. Re-run on the same instance (same key): hit, same stamp.
    /// </summary>
    [SkippableFact]
    public async Task SourceInputResolvesUrl_MissThenHit()
    {
        Skip.If(!await IsMockLabUpAsync(),
            $"MockLab is not reachable at {MockLabBaseUrl()} — start it with `docker compose up -d` " +
            "in the repo root (or set MOCKLAB_BASE_URL).");

        const string caseKey = "case-cacheaside-key-de-nat";
        var instanceKey = NewKey("til-ca-src");
        var instanceId = await StartKeyedAsync(instanceKey, new { });

        var first = await RunCacheCaseAsync(instanceId, caseKey);
        AssertCacheFlags(first, hit: false, "first run on a fresh per-instance key must miss");
        AssertShaped(first);
        Assert.Equal("til-cache-source-value",
            first.GetProperty("tilData").GetProperty("payload").GetProperty("value").GetString());
        var firstStamp = ComputedAt(first);

        var second = await RunCacheCaseAsync(instanceId, caseKey);
        AssertCacheFlags(second, hit: true, "re-run against the same key must hit the entry the first run wrote");
        AssertShaped(second);
        Assert.Equal("til-cache-source-value",
            second.GetProperty("tilData").GetProperty("payload").GetProperty("value").GetString());
        Assert.Equal(firstStamp, ComputedAt(second));
        Assert.Equal($"custom:til:ca:{instanceKey}:de-nat", CacheKey(second));
    }

    /// <summary>
    /// vnext #1048 acceptance: "A type-18 task whose <c>sourceTask</c> has no static key, with a
    /// <c>sourceMapping</c> whose InputHandler calls <c>SetKey(...)</c>, fills the cache on a miss and
    /// serves subsequent hits." <c>til-getdata-source</c> (GetInstanceData, type 13) has no key in its
    /// config; <c>TilCacheSourceMapping.InputHandler</c> sets it from this instance's <c>targetKey</c>,
    /// pointing at a second task-invocation-lab instance this test starts with a unique marker. Before
    /// the fix the source called <c>/instances//data</c>.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataSource_SetKey_FillsCacheAndServesHit()
    {
        var targetKey = NewKey("til-ca-target");
        var marker = NewKey("til-getdata-marker");
        await StartKeyedAsync(targetKey, new { tilTargetMarker = marker });

        const string caseKey = "case-cacheaside-getdata";
        var instanceKey = NewKey("til-ca-getdata");
        var instanceId = await StartKeyedAsync(instanceKey, new { targetKey });

        var first = await RunCacheCaseAsync(instanceId, caseKey);
        AssertCacheFlags(first, hit: false, "first run must miss and fill the cache");
        AssertShaped(first);
        Assert.Equal($"custom:til:ca:{instanceKey}:getdata", CacheKey(first));
        Assert.Contains(marker, Payload(first));
        var firstStamp = ComputedAt(first);

        var second = await RunCacheCaseAsync(instanceId, caseKey);
        AssertCacheFlags(second, hit: true, "subsequent run must be served from the cache");
        AssertShaped(second);
        Assert.Equal($"custom:til:ca:{instanceKey}:getdata", CacheKey(second));
        Assert.Contains(marker, Payload(second));
        Assert.Equal(firstStamp, ComputedAt(second));
    }

    /// <summary>
    /// Every key-script kind computes the per-instance key: Dynamic Expresso and C#
    /// <c>ICacheKeyMapping</c>, each embedded as NAT, B64 and REF (the REF bodies are the
    /// <c>til-cache-key-de</c> / <c>til-cache-key-cs</c> sys-mappings). The metadata <c>Key</c> is the
    /// STORE key, so it carries the state store's <c>custom:</c> prefix.
    /// </summary>
    [SkippableTheory]
    [InlineData("de-nat")]
    [InlineData("de-b64")]
    [InlineData("de-ref")]
    [InlineData("cs-nat")]
    [InlineData("cs-b64")]
    [InlineData("cs-ref")]
    public async Task KeyScript_AllKinds(string variant)
    {
        Skip.If(!await IsMockLabUpAsync(),
            $"MockLab is not reachable at {MockLabBaseUrl()} — every key-script case reads through " +
            "til-cache-source on its miss. Start it with `docker compose up -d` in the repo root.");

        var instanceKey = NewKey($"til-ca-{variant}");
        var instanceId = await StartKeyedAsync(instanceKey, new { });

        var projection = await RunCacheCaseAsync(instanceId, $"case-cacheaside-key-{variant}");

        Assert.Equal($"custom:til:ca:{instanceKey}:{variant}", CacheKey(projection));
        AssertCacheFlags(projection, hit: false, "a fresh per-instance key cannot already be cached");
        AssertShaped(projection);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string NewKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Starts an instance with an explicit key and start attributes, parked on <c>ready</c>.</summary>
    private async Task<string> StartKeyedAsync(string key, object attributes)
    {
        var instanceId = await StartAsync(Workflow, new { key, attributes });
        await WaitUntilSettledAsync(Workflow, instanceId);
        return instanceId;
    }

    /// <summary>
    /// Fires a <c>$self</c> cache case, waits for it to settle, asserts the instance is still on
    /// <c>ready</c> with no incident, and returns the projection that case wrote.
    /// </summary>
    private async Task<JsonElement> RunCacheCaseAsync(string instanceId, string caseKey)
    {
        await RunAcceptedAsync(Workflow, instanceId, caseKey);

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.True(status != "F", $"{caseKey} faulted the instance — " + await DescribeAsync(Workflow, instanceId));
        Assert.Equal(ReadyState, state);
        Assert.Empty(await GetIncidentItemsAsync(instanceId));

        var projection = await GetProjectionAsync(instanceId);
        Assert.Equal(caseKey, NullableString(projection, "tilCase"));
        return projection;
    }

    private static JsonElement Metadata(JsonElement projection)
    {
        Assert.True(projection.TryGetProperty("tilMetadata", out var metadata) &&
                    metadata.ValueKind == JsonValueKind.Object,
            "tilMetadata missing — the transition-level OutputHandler did not see the CacheAside metadata");
        return metadata;
    }

    private static void AssertCacheFlags(JsonElement projection, bool hit, string because)
    {
        var metadata = Metadata(projection);
        Assert.True(Flag(metadata, "cacheHit") == hit, $"cacheHit should be {hit}: {because}. tilMetadata: {metadata.GetRawText()}");
        // A miss refreshes the entry; a hit does not.
        Assert.True(Flag(metadata, "refreshed") == !hit, $"refreshed should be {!hit}. tilMetadata: {metadata.GetRawText()}");
    }

    private static string CacheKey(JsonElement projection) => Text(Metadata(projection), "key");

    private static void AssertShaped(JsonElement projection)
    {
        Assert.True(projection.TryGetProperty("tilData", out var data) && data.ValueKind == JsonValueKind.Object,
            "tilData should be the cached (shaped) object");
        Assert.True(Flag(data, "shaped"),
            $"tilData.shaped should be true — the cached value must be TilCacheSourceMapping's output: {data.GetRawText()}");
    }

    private static string ComputedAt(JsonElement projection) =>
        projection.GetProperty("tilData").GetProperty("computedAtUtc").GetRawText();

    private static string Payload(JsonElement projection) =>
        projection.GetProperty("tilData").TryGetProperty("payload", out var payload) ? payload.GetRawText() : string.Empty;
}
