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
        // Workflow keys in this domain: account-opening, money-transfer, payment-process,
        // scheduled-payments, loan-disbursement, credit-bureau-inquiry, collateral-establishment, …
        var response = await Api.ListInstancesAsync("account-opening");
        Assert.True(response.Body.ValueKind != System.Text.Json.JsonValueKind.Null,
            "Expected a non-null response from ListInstances");
    }
}
