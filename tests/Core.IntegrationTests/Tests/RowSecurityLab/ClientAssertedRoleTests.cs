using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Client-asserted role (<c>rs-a</c>). Row security trusts the header set the request arrives with; the gateway
/// must strip or overwrite <c>role</c> (docs/domain/row-security.md § Gateway precondition).
/// <list type="bullet">
/// <item>H-1 [pin]: under the default provider a client-sent <c>role: hq-auditor</c> IS the role set ⇒ all rows.
/// Known gap closed only by the gateway precondition; asserts current behaviour.</item>
/// <item>H-4: <c>?role=hq-auditor</c> is not a role source for list/GET (it is an <c>authorize</c> parameter only)
/// ⇒ a <c>branch-staff</c> BR-001 caller still sees own branch <c>{r1, r2, r5}</c> (R-F2). Runs under both providers
/// (the role header replaces the IDM answer under morph-idm).</item>
/// </list>
/// </summary>
public class ClientAssertedRoleTests : RowSecurityLabTestBase
{
    private const string Flow = "rs-a";

    public ClientAssertedRoleTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>H-1 [pin]: alice + client-sent <c>role: hq-auditor</c> ⇒ all seven rows (gateway precondition gap).</summary>
    [SkippableFact]
    public async Task H1_Pinned_ClientAssertedHqAuditorHeader_SeesAll()
    {
        Skip.IfNot(ProviderIs("default"), "D run: under morph-idm the H-1 counterpart is H-2 (11G)");
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(Sub: "alice", ActSub: "alice", Role: HqAuditor), [.. p.All]);
    }

    /// <summary>H-4: <c>?role=hq-auditor</c> on list/GET does not widen a <c>branch-staff</c> caller.</summary>
    [Fact]
    public async Task H4_RoleQueryParameter_DoesNotWiden()
    {
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p,
            new Caller(Sub: "alice", ActSub: "alice", Role: "branch-staff",
                Headers: new Dictionary<string, string> { ["x-branch-code"] = "BR-001" },
                Query: "role=hq-auditor"),
            p.R1, p.R2, p.R5);
    }
}
