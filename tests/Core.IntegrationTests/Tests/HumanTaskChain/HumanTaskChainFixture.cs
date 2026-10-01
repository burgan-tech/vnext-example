using VNext.Testing.Sdk.Infrastructure;

namespace Core.IntegrationTests.Tests.HumanTaskChain;

/// <summary>
/// Publishes the <c>credit</c> domain's components (<c>credit/</c>, described by
/// <c>vnext.credit.config.json</c>) to the credit orchestrator once per test run.
/// </summary>
/// <remarks>
/// Same shape and same reason as <c>CrossDomainLabFixture</c>: in external-stack mode the SDK never
/// calls <c>OnAfterEnvironmentReadyAsync</c>, so anything beyond the <c>core</c> publish has to be
/// done by a fixture. <c>partner</c> is published by <c>CrossDomainLabFixture</c>, which these tests
/// also take — credit is the only domain this one owns, so the two never publish the same components.
/// </remarks>
public sealed class HumanTaskChainFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _published;

    /// <summary>Credit orchestrator base url, or null when the lab's third business domain is absent.</summary>
    public string? CreditBaseUrl { get; } =
        Environment.GetEnvironmentVariable("VNEXT_CREDIT_BASE_URL")?.Trim().TrimEnd('/') is { Length: > 0 } url ? url : null;

    public async Task InitializeAsync()
    {
        if (CreditBaseUrl is null) return;

        await Gate.WaitAsync();
        try
        {
            if (_published) return;
            Console.WriteLine($"[HumanTaskChain] publishing credit components to {CreditBaseUrl}");
            await LocalDomainPublisher.PublishAsync(CreditBaseUrl, "credit", "vnext.credit.config.json");
            _published = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
