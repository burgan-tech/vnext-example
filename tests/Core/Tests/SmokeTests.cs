using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests;

/// <summary>
/// Smoke tests — verify the environment is up and the API is reachable.
/// These run first and catch infrastructure issues before domain tests execute.
/// </summary>
public class SmokeTests : IntegrationTestBase
{
    public SmokeTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var response = await Api.GetRawAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListInstances_ReturnsValidResponse()
    {
        // Replace "my-workflow" with a workflow name that exists in your domain.
        var response = await Api.ListInstancesAsync("my-workflow");
        Assert.True(response.Body.ValueKind != System.Text.Json.JsonValueKind.Null,
            "Expected a non-null response from ListInstances");
    }
}
