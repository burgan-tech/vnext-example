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

    /// <summary>
    /// The instance's <c>metadata.modifiedAt</c> and <c>metadata.completedAt</c> (null until it
    /// ends; the runtime omits null members), with its own state and status.
    /// </summary>
    protected async Task<(DateTimeOffset ModifiedAt, DateTimeOffset? CompletedAt, string State, string Status)>
        GetInstanceTimesAsync(string workflow, string instanceId, Dictionary<string, string>? headers = null)
    {
        var response = await Api.GetInstanceAsync(workflow, instanceId, headers);
        var metadata = response.Body.GetProperty("metadata");
        DateTimeOffset? completedAt =
            metadata.TryGetProperty("completedAt", out var completed) && completed.ValueKind == System.Text.Json.JsonValueKind.String
                ? completed.GetDateTimeOffset()
                : null;

        return (metadata.GetProperty("modifiedAt").GetDateTimeOffset(),
                completedAt,
                metadata.GetProperty("currentState").GetString() ?? "",
                metadata.GetProperty("status").GetString() ?? "");
    }
}

/// <summary>
/// xUnit collection definition — shares one <see cref="VNextTestEnvironment"/> across all
/// test classes in this project.
/// </summary>
[CollectionDefinition("VNextIntegration")]
public class VNextIntegrationCollection : ICollectionFixture<VNextTestEnvironment>
{
}
