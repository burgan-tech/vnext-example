using VNext.Testing.Sdk.Client;

namespace Core.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for all Core integration tests.
/// Inherits shared fixtures from <see cref="VNext.Testing.Sdk.Infrastructure.IntegrationTestBase{TEnvironment}"/>.
/// </summary>
[Collection("VNextIntegration")]
public abstract class IntegrationTestBase
    : VNext.Testing.Sdk.Infrastructure.IntegrationTestBase<VNextTestEnvironment>
{
    protected IntegrationTestBase(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// Creates a client pre-configured for the "core" domain.
    /// Override to add auth headers or custom behaviour.
    /// </summary>
    protected override VNextApiClient CreateApiClient(string baseUrl) =>
        new(new VNextApiClientOptions
        {
            BaseUrl = baseUrl,
            Domain = "core",
            ApiVersion = "1"
        });
}

/// <summary>
/// xUnit collection definition — shares one <see cref="VNextTestEnvironment"/> across all
/// test classes in this project.
/// </summary>
[CollectionDefinition("VNextIntegration")]
public class VNextIntegrationCollection : ICollectionFixture<VNextTestEnvironment>
{
}
