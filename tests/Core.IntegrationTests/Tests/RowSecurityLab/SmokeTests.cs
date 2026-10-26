using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Smoke row of the row-security lab: <c>rs-a</c> declares <c>rowSecurity</c> with no <c>default</c> (deny by
/// default, K1). A caller with an identity and no role matches no rule ⇒ empty list and 404 on every GET. A runtime
/// without row security ignores the unknown member and serves all seven rows (AR-4), so this row is red there.
/// </summary>
public class SmokeTests : RowSecurityLabTestBase
{
    private const string Flow = "rs-a";

    public SmokeTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task RowSecurityLab_Smoke_RolelessDenyListIsEmpty()
    {
        var population = await SeedAsync(Flow);

        var roleless = new Caller(Sub: "alice", ActSub: "alice");

        Assert.Empty(await ListIdsAsync(Flow, roleless));
        await AssertVisibleAsync(Flow, population, roleless);
    }
}
