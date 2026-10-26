using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Identity-bound rules: <c>$InstanceStarter</c> → <c>createdBy eq ActorUserName</c> (<c>act_sub</c>),
/// <c>$InstanceBehalfOfStarter</c> → <c>createdByBehalfOf eq UserName</c> (<c>sub</c>). Same pairing as role grants.
/// <list type="bullet">
/// <item><c>rs-s</c>: <c>hq-auditor</c> + bare <c>$InstanceStarter</c>.</item>
/// <item><c>rs-behalf</c>: <c>hq-auditor</c> + <c>$InstanceStarter</c> AND <c>status eq A</c> +
/// bare <c>$InstanceBehalfOfStarter</c>.</item>
/// </list>
/// Population: r1 act_sub alice; r2 carol; r3 act_sub alice / sub bob; r4 carol, closed (C). Default-provider run only.
/// </summary>
public class IdentityRuleTests : RowSecurityLabTestBase
{
    private const string SkipReason = "D run: identity rows are asserted under the default caller-role provider";

    public IdentityRuleTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>S-1: <c>rs-s</c> (identity rule only), alice with no role ⇒ <c>{r1, r3}</c>.</summary>
    [SkippableFact]
    public async Task S1_IdentityRuleOnly_RolelessStarterSeesOwnInstances()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        const string flow = "rs-s";
        var p = await SeedAsync(flow);

        await AssertVisibleAsync(flow, p, new Caller(Sub: "alice", ActSub: "alice"), p.R1, p.R3);
    }

    /// <summary>I-1: <c>rs-behalf</c>, <c>sub: bob</c> only ⇒ <c>{r3}</c> (createdByBehalfOf = bob).</summary>
    [SkippableFact]
    public async Task I1_BehalfOfStarter_SubBobSeesRowStartedOnHisBehalf()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        const string flow = "rs-behalf";
        var p = await SeedAsync(flow);

        await AssertVisibleAsync(flow, p, new Caller(Sub: "bob"), p.R3);
    }

    /// <summary>
    /// I-2: <c>rs-behalf</c>, <c>act_sub: carol</c> only ⇒ <c>{r2}</c>: carol started r2 and r4, but the
    /// <c>$InstanceStarter</c> rule ANDs <c>status eq A</c> and r4 is closed.
    /// </summary>
    [SkippableFact]
    public async Task I2_InstanceStarterWithStatusFilter_ActSubCarolSeesOnlyActiveOwnRow()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        const string flow = "rs-behalf";
        var p = await SeedAsync(flow);

        await AssertVisibleAsync(flow, p, new Caller(ActSub: "carol"), p.R2);
    }

    /// <summary>I-3: <c>rs-behalf</c>, blank <c>act_sub</c> ⇒ none (a blank identity adds no identity term).</summary>
    [SkippableFact]
    public async Task I3_BlankActSub_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        const string flow = "rs-behalf";
        var p = await SeedAsync(flow);

        await AssertVisibleAsync(flow, p, new Caller(ActSub: ""));
    }

    /// <summary>
    /// I-4: the identity columns the rules compare against are what the seed wrote:
    /// <c>metadata.createdBy</c>(r1) = alice, <c>metadata.createdByBehalfOf</c>(r3) = bob.
    /// </summary>
    [SkippableFact]
    public async Task I4_IdentityColumns_CarryStarterAndBehalfOf()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        const string flow = "rs-behalf";
        var p = await SeedAsync(flow);

        Assert.Equal("alice", await MetadataAsync(flow, p.R1, "createdBy"));
        Assert.Equal("bob", await MetadataAsync(flow, p.R3, "createdByBehalfOf"));
    }

    private async Task<string?> MetadataAsync(string flow, Guid id, string member)
    {
        var (status, body, _) = await GetAsync(flow, id.ToString(), Hq());
        Assert.True(status == HttpStatusCode.OK, $"GET {flow}/{id} as hq answered {(int)status}: {body}");
        var metadata = JsonDocument.Parse(body).RootElement.GetProperty("metadata");
        return metadata.TryGetProperty(member, out var value) ? value.GetString() : null;
    }
}
