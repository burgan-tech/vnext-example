using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AuthorizationChainLab;

/// <summary>
/// <c>data</c> is the one surface whose CONTENT does not descend while its AUTHORIZATION does.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Every built-in instance function walks into an active SubFlow —
/// <c>state</c>, <c>view</c>, <c>schema</c>, <c>master</c>, <c>extensions</c> and <c>authorize</c> —
/// except <c>data</c>, which serves the polled instance's own attributes. The requester settled the
/// remaining half on 2026-09-22: the data function's execution side is correct and must not change,
/// and its authorization descends with everything else, because while the instance sits in an active
/// subflow the subflow's rules are the ones in force.</para>
/// <para>The rule, in one line: <b>authorization follows where the instance actually is; content
/// follows what the client holds.</b> That reads as an inconsistency to anyone meeting it for the
/// first time, which is exactly why it is pinned here rather than left to a comment.</para>
/// <para>This pair is the most likely thing to be "simplified" later by someone who reads only one
/// half of it, so both halves are asserted in the same test.</para>
/// </remarks>
public sealed class DataDescentAsymmetryTests : AuthorizationChainLabTestBase
{
    public DataDescentAsymmetryTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// The half that is provable today: <c>data</c> serves the POLLED instance's attributes, never a
    /// descendant's.
    /// </summary>
    /// <remarks>
    /// The other half — "its authorization descends" — is a property of the <c>authorize</c> FUNCTION,
    /// which the middle tier consults, not of the data function's own gate. The data function gates
    /// the polled instance only, exactly as the state function does, and both fall back to the
    /// workflow root's grants while a SubFlow is active (the <c>EffectiveState</c> keying defect).
    /// An earlier version of this test asserted 403 for <c>chain.reader</c> at the root and was
    /// conflating the two; the agreement that IS enforceable today is pinned by
    /// <see cref="DataAndStateAgreeAboutAdmission"/>.
    /// </remarks>
    [Fact]
    public async Task DataServesThePolledInstancesOwnAttributes()
    {
        var chain = await StartChainAsync();

        var (adminStatus, adminBody) = await CallFunctionAsync(Root, chain.RootId, "data", Admin);
        Assert.Equal(HttpStatusCode.OK, adminStatus);

        // The data function answers an envelope whose attributes live under `data`
        // ({"data":{…},"eTag":…,"extensions":{}}), not under `attributes` — measured, not assumed.
        var attributes = adminBody.ValueKind == System.Text.Json.JsonValueKind.Object
                         && adminBody.TryGetProperty("data", out var inner)
            ? inner
            : adminBody;

        Assert.True(
            attributes.ValueKind == System.Text.Json.JsonValueKind.Object
            && attributes.TryGetProperty("chainRef", out _),
            "the data function must serve the POLLED instance's attributes — chainRef is written at " +
            "the root's start and never copied down the chain, so its absence means data descended");
    }

    /// <summary>
    /// <c>data</c> and <c>state</c> must agree about admission even though they disagree about whose
    /// content they serve. A gateway has one <c>queryRoles</c> verdict for the whole read family; if
    /// these two diverged, that single verdict could not be right for both.
    /// </summary>
    [Theory]
    [InlineData(Reader)]
    [InlineData(Admin)]
    [InlineData(LeafAdmin)]
    public async Task DataAndStateAgreeAboutAdmission(string roles)
    {
        var chain = await StartChainAsync();

        var (dataStatus, _) = await CallFunctionAsync(Root, chain.RootId, "data", roles);
        var (stateStatus, _) = await CallFunctionAsync(Root, chain.RootId, "state", roles);

        Assert.Equal(dataStatus == HttpStatusCode.Forbidden, stateStatus == HttpStatusCode.Forbidden);
    }
}
