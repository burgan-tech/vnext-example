using Core.IntegrationTests.Infrastructure;
using VNext.Testing.Sdk.Infrastructure;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// Publishes the <c>partner</c> domain's components (<c>partner/</c>, described by
/// <c>vnext.partner.config.json</c>) to the partner orchestrator once per test run.
/// </summary>
/// <remarks>
/// This cannot live in <c>VNextTestEnvironment.OnAfterEnvironmentReadyAsync</c>: in the external-stack
/// mode the lab runs in (<c>VNEXT_BASE_URL</c> set) the SDK never calls that hook — see the note in
/// Infrastructure/VNextTestEnvironment.cs. A class fixture is the next-earliest deterministic point,
/// and a static gate keeps the publish to one execution even though two test classes use it.
/// <para>
/// <c>LocalDomainPublisher</c> rewrites <c>domain</c>/<c>version</c> in every component but leaves
/// <c>config</c> and <c>process</c> alone, so the parent's <c>config.domain: partner</c> references
/// survive the core publish and the partner's own components land under <c>partner</c>.
/// </para>
/// </remarks>
public sealed class CrossDomainLabFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _published;

    /// <summary>
    /// Partner orchestrator base url, or null when the lab is not configured OR not running — the
    /// url is committed in test.runsettings, so "set" alone proves nothing (see OptionalDomainEndpoint).
    /// </summary>
    public string? PartnerBaseUrl { get; } = OptionalDomainEndpoint.Resolve("VNEXT_PARTNER_BASE_URL", "partner");

    public async Task InitializeAsync()
    {
        if (PartnerBaseUrl is null) return;

        await Gate.WaitAsync();
        try
        {
            if (_published) return;
            Console.WriteLine($"[CrossDomainLab] publishing partner components to {PartnerBaseUrl}");
            await LocalDomainPublisher.PublishAsync(PartnerBaseUrl, "partner", "vnext.partner.config.json");
            _published = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
