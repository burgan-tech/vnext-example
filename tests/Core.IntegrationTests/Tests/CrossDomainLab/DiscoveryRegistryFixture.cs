namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// Holds the discovery registry's base url and a client for it, so the warm-up tests can read the
/// registry directly and compare it with what the runtime under test did with the same data.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="CrossDomainLabFixture"/> and gated on its own environment
/// variable. The registry warm-up needs only <c>core</c> plus the registry — not the <c>partner</c>
/// domain — so it runs in a two-domain setup as well as in the full three-domain lab, and it must not
/// be skipped just because <c>VNEXT_PARTNER_BASE_URL</c> is absent.
/// </remarks>
public sealed class DiscoveryRegistryFixture : IDisposable
{
    /// <summary>Discovery registry orchestrator base url, or null when no registry is configured.</summary>
    public string? RegistryBaseUrl { get; } =
        Environment.GetEnvironmentVariable("VNEXT_DISCOVERY_BASE_URL")?.Trim().TrimEnd('/') is { Length: > 0 } url
            ? url
            : null;

    /// <summary>The registry domain, which is also the first path segment of its function urls.</summary>
    public string RegistryDomain { get; } =
        Environment.GetEnvironmentVariable("VNEXT_DISCOVERY_DOMAIN")?.Trim() is { Length: > 0 } domain
            ? domain
            : "discovery";

    private HttpClient? _client;

    /// <summary>Client on the registry. Only valid when <see cref="RegistryBaseUrl"/> is set.</summary>
    public HttpClient Client => _client ??= new HttpClient
    {
        BaseAddress = new Uri((RegistryBaseUrl ?? throw new InvalidOperationException(
            "registry client unavailable — VNEXT_DISCOVERY_BASE_URL is not set")) + "/")
    };

    public void Dispose() => _client?.Dispose();
}
