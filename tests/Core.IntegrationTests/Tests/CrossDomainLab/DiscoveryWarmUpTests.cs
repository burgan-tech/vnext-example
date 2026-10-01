using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// AC-12..14 — the discovery endpoint cache's bulk warm-up reads the registry's <c>domain-list</c>
/// function and publishes what it reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The runtime used to enumerate the registry by paging its raw instance
/// list (<c>/{domain}/workflows/domain/instances?page=…</c>) and re-deriving the domain projection
/// client-side — status filtering, key-versus-<c>domainName</c> fallback, page-stall detection, a
/// page cap. The registry now owns that projection in a Domain-scope, server-cached function, so the
/// runtime reads it once per refresh window instead (vnext, 2026-09-17). There is deliberately no
/// fallback to the old paged read, which makes "does the runtime actually speak to this function,
/// and does it do the right thing with the answer" the one question unit tests cannot settle: they
/// pin the parsing against a captured payload, not against a live registry.
/// </para>
/// <para>
/// <b>What it needs.</b> A discovery registry carrying <c>@burgan-tech/vnext-discovery-runtime</c>
/// &gt;= 0.0.7 (the first version with <c>domain-list</c>) at <c>VNEXT_DISCOVERY_BASE_URL</c>, and a
/// core runtime configured against it with <c>ServiceDiscovery__Enabled=true</c>,
/// <c>ServiceDiscovery__Provider=http</c> and <c>ServiceDiscovery__Cache__Enabled=true</c>. The cache
/// is registered only for the <c>http</c> provider — the Dapr provider derives app-ids from a naming
/// convention and never reads the registry on its default path — so under
/// <c>Provider=dapr</c> the refresh endpoint answers <c>disabled</c> and AC-13/AC-14 skip
/// themselves rather than fail.
/// </para>
/// </remarks>
[Collection("VNextIntegration")]
public class DiscoveryWarmUpTests : WorkflowTestBase, IClassFixture<DiscoveryRegistryFixture>
{
    private const string RefreshPath = "api/v1/utilities/discovery/refresh";

    private readonly DiscoveryRegistryFixture _registry;

    public DiscoveryWarmUpTests(VNextTestEnvironment environment, DiscoveryRegistryFixture registry)
        : base(environment)
    {
        _registry = registry;
    }

    /// <summary>
    /// AC-12: the registry's <c>domain-list</c> answers the four-field contract the runtime's bulk
    /// read is written against — one flat <c>items</c> array, no pagination envelope.
    /// </summary>
    [SkippableFact]
    public async Task DomainList_ServesTheFlatFourFieldContract()
    {
        RequireRegistry();

        var (status, body) = await GetRegistryAsync(DomainListPath);

        Assert.Equal(HttpStatusCode.OK, status);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // The envelope is deliberately NOT the instance-list one ({ links, items: [ { key, metadata,
        // attributes } ] }). A regression to that shape would leave every domainName empty and the
        // runtime would warm nothing while still reporting success.
        Assert.False(root.TryGetProperty("links", out _), $"domain-list must not carry a pagination envelope: {body}");
        Assert.True(root.TryGetProperty("items", out var items), $"no items array in: {body}");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);

        var listed = items.EnumerateArray().ToList();
        Assert.True(listed.Count > 0,
            "the registry reported no domains at all; the runtime treats an empty list as a failed refresh window");

        foreach (var item in listed)
        {
            Assert.True(NonEmpty(item, "domainName"), $"item without domainName: {item}");
            Assert.True(NonEmpty(item, "baseUrl"), $"item without baseUrl: {item}");

            // The fields the runtime maps onto DomainRegistration. appId/healthUrl may be empty for a
            // registration that never carried them, but the properties must exist.
            Assert.True(item.TryGetProperty("appId", out _), $"item without appId: {item}");
            Assert.True(item.TryGetProperty("healthUrl", out _), $"item without healthUrl: {item}");
        }
    }

    /// <summary>
    /// AC-13: a forced refresh reads that list and publishes it. <c>Refreshed</c> is the assertion
    /// with teeth — the refresher reports it only when the read succeeded AND returned a non-empty
    /// list AND every entry was written to the cache; a 404, an unparsable body or an empty list all
    /// come back as <c>Failed</c>.
    /// </summary>
    [SkippableFact]
    public async Task ForcedRefresh_ReadsTheDomainListAndPublishesTheCache()
    {
        RequireRegistry();

        var (status, body) = await SendRawAsync(HttpMethod.Post, RefreshPath);

        Assert.Equal(HttpStatusCode.OK, status);

        var outcome = Outcome(body);
        RequireCacheEnabled(outcome, body);

        Assert.True(outcome == "Refreshed",
            $"the warm-up did not read the registry's domain-list: {body}. " +
            "Failed here means the runtime's bulk read did not come back with a usable list — check the " +
            "orchestration log for DomainListEndpointMissing (the registry package predates domain-list) " +
            "or a non-2xx from the registry.");
    }

    /// <summary>
    /// AC-14: what the runtime warms follows the registry. A domain registered now appears in
    /// <c>domain-list</c> — the registry evicts its own 24 h function cache on registration — and the
    /// very next forced refresh still succeeds, so the runtime is reading the live list rather than
    /// a value frozen at its own startup.
    /// </summary>
    [SkippableFact]
    public async Task NewRegistration_IsVisibleToTheNextWarmUp()
    {
        RequireRegistry();

        // Skip before writing anything to the registry if the cache is off; otherwise the test would
        // leave a synthetic domain behind for a run that could never assert on it.
        var (_, probe) = await SendRawAsync(HttpMethod.Post, RefreshPath);
        RequireCacheEnabled(Outcome(probe), probe);

        var domain = $"warmup-probe-{Guid.NewGuid():N}"[..24];
        var baseUrl = $"http://{domain}.invalid:5000";

        var registered = await RegisterDomainAsync(domain, baseUrl);
        Assert.True(registered.Status is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.Created,
            $"could not register the probe domain: {(int)registered.Status} {registered.Body}");

        var (_, listBody) = await GetRegistryAsync(DomainListPath);
        using var document = JsonDocument.Parse(listBody);

        var entry = document.RootElement.GetProperty("items").EnumerateArray()
            .FirstOrDefault(i => i.GetProperty("domainName").GetString() == domain);

        Assert.True(entry.ValueKind == JsonValueKind.Object,
            $"'{domain}' was registered but is not in domain-list; the registry's function cache " +
            $"(discovery:domains:active) was not evicted by the registration: {listBody}");
        Assert.Equal(baseUrl, entry.GetProperty("baseUrl").GetString());

        var (status, body) = await SendRawAsync(HttpMethod.Post, RefreshPath);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(Outcome(body) == "Refreshed", $"the warm-up failed on the grown list: {body}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string DomainListPath => $"api/v1/{_registry.RegistryDomain}/functions/domain-list";

    private void RequireRegistry() =>
        Skip.If(_registry.RegistryBaseUrl is null,
            "VNEXT_DISCOVERY_BASE_URL is not set — a discovery registry carrying " +
            "@burgan-tech/vnext-discovery-runtime >= 0.0.7 is required.");

    /// <summary>
    /// The refresh endpoint answers <c>disabled</c> when no <c>IDiscoveryCacheRefresher</c> is
    /// registered, which is the correct state under <c>Provider=dapr</c> or with the cache switched
    /// off. That is a configuration, not a defect, so the test steps aside instead of failing.
    /// </summary>
    private static void RequireCacheEnabled(string? outcome, string body) =>
        Skip.If(outcome == "disabled",
            "the discovery cache is not enabled on this runtime — run it with " +
            $"ServiceDiscovery__Provider=http and ServiceDiscovery__Cache__Enabled=true ({body})");

    private static string? Outcome(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("outcome", out var outcome) ? outcome.GetString() : null;
    }

    private static bool NonEmpty(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && !string.IsNullOrWhiteSpace(value.GetString());

    private async Task<(HttpStatusCode Status, string Body)> GetRegistryAsync(string path)
    {
        using var response = await _registry.Client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Registers a domain the way the runtime's own <c>DomainRegistrationService</c> does: a
    /// synchronous start of the registry's <c>domain-registration</c> flow, instance key = domain name.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> RegisterDomainAsync(string domain, string baseUrl)
    {
        var payload = new
        {
            key = domain,
            tags = new[] { "domain", "registration", "integration-test" },
            attributes = new
            {
                domainName = domain,
                baseUrl,
                healthUrl = $"{baseUrl}/health",
                appId = $"vnext-{domain}-app"
            }
        };

        using var response = await _registry.Client.PostAsJsonAsync(
            $"api/v1/{_registry.RegistryDomain}/workflows/domain-registration/instances/start?sync=true",
            payload);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
