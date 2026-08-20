using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// Master-schema <c>x-roles</c> — field-level pruning on the way out.
/// <para>
/// This is the finest-grained gate in the system and the only one where a caller gets a 200 that is
/// quietly missing data. The master schema guards two fields with deliberately different grant
/// shapes:
/// <list type="bullet">
///   <item><c>decisionNote</c> — approver and auditor ALLOW, maker DENY</item>
///   <item><c>auditTrail</c> — a single ALLOW for the auditor, so everyone else is pruned</item>
/// </list>
/// <c>caseRef</c> is unguarded and acts as the control: its presence proves the read itself
/// succeeded and only the guarded fields were removed. Without it, a pruned field and an empty
/// response look the same.
/// </para>
/// </summary>
public class SchemaFieldVisibilityTests : RoleMatrixLabTestBase
{
    public SchemaFieldVisibilityTests(VNextTestEnvironment environment) : base(environment) { }

    // ── the control ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnguardedField_IsVisibleToEveryCallerThatPassesTheQueryGate()
    {
        var instanceId = await StartCaseAsync("xroles-control");

        foreach (var role in new[] { Maker, Approver, Auditor })
        {
            var (status, attributes) = await GetDataAttributesAsync(instanceId, role);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(Has(attributes, "caseRef"),
                $"{role} lost the unguarded caseRef field: {attributes}");
        }
    }

    // ── allowlist field ──────────────────────────────────────────────────────

    /// <summary>
    /// <c>auditTrail</c> carries a single ALLOW. Only the auditor sees it — including the approver,
    /// who decides the case and is otherwise the most privileged reader in this state.
    /// </summary>
    [Fact]
    public async Task AnAllowlistField_ReachesOnlyTheGrantedRole()
    {
        var instanceId = await StartCaseAsync("xroles-allowlist");

        var (_, auditor) = await GetDataAttributesAsync(instanceId, Auditor);
        Assert.True(Has(auditor, "auditTrail"), $"the auditor lost auditTrail: {auditor}");

        foreach (var role in new[] { Maker, Approver })
        {
            var (_, attributes) = await GetDataAttributesAsync(instanceId, role);
            Assert.False(Has(attributes, "auditTrail"),
                $"{role} received auditTrail, which only the auditor is granted: {attributes}");
        }
    }

    // ── deny-bearing field ───────────────────────────────────────────────────

    [Fact]
    public async Task ADenyBearingField_ReachesTheAllowedRolesAndNotTheDeniedOne()
    {
        var instanceId = await StartCaseAsync("xroles-deny");

        foreach (var role in new[] { Approver, Auditor })
        {
            var (_, attributes) = await GetDataAttributesAsync(instanceId, role);
            Assert.True(Has(attributes, "decisionNote"),
                $"{role} lost decisionNote, which it is granted: {attributes}");
        }

        var (_, maker) = await GetDataAttributesAsync(instanceId, Maker);
        Assert.False(Has(maker, "decisionNote"),
            $"the maker received decisionNote despite an explicit DENY: {maker}");
    }

    /// <summary>
    /// DENY wins at the FIELD level even when the same caller is admitted by another of its roles.
    /// This is the sharp contrast with the instance-level gate, where a caller holding both maker
    /// and approver reads the instance successfully: there, one ALLOW is enough; here, the maker's
    /// DENY still removes the field from that same caller's response.
    /// </summary>
    [Fact]
    public async Task ACallerHoldingBothAnAllowedAndADeniedRole_LosesTheField()
    {
        var instanceId = await StartCaseAsync("xroles-deny-wins");

        var (status, attributes) = await GetDataAttributesAsync(instanceId, $"{Maker},{Approver}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(Has(attributes, "caseRef"), "the read itself should have succeeded");
        Assert.False(Has(attributes, "decisionNote"),
            $"DENY did not win at the field level for a maker+approver caller: {attributes}");
    }

    // ── consistency across surfaces ──────────────────────────────────────────

    /// <summary>
    /// The view function must not become a side channel for guarded data.
    /// <para>
    /// Today it cannot be one: <c>GetViewOutput</c> carries the view definition only — key, content,
    /// type, display, label, renderer — and has no slot for instance data, so <c>loadData: true</c>
    /// is a signal to the client to fetch the data function itself rather than a promise to inline
    /// it. This test pins that shape. If a future change starts inlining instance data here, this
    /// fails and whoever makes that change has to apply <c>x-roles</c> pruning on the way — which is
    /// exactly the review this assertion is meant to force.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheViewFunction_DoesNotCarryGuardedInstanceData()
    {
        var instanceId = await StartCaseInReviewAsync("xroles-view");

        var (status, body) = await CallInstanceFunctionAsync(instanceId, "view", Approver);
        Assert.Equal(HttpStatusCode.OK, status);

        var data = FindInstanceData(body);

        Assert.True(data is null,
            $"the view response now inlines instance data ({data}); x-roles pruning must be applied " +
            "to it before this test is relaxed");
    }

    /// <summary>
    /// Pruning is applied on the way out and must not touch what is stored. The auditor still sees
    /// both guarded fields after another caller has read the instance and had them removed.
    /// </summary>
    [Fact]
    public async Task PruningDoesNotMutateStoredData()
    {
        var instanceId = await StartCaseAsync("xroles-non-destructive");

        await GetDataAttributesAsync(instanceId, Maker);
        await GetDataAttributesAsync(instanceId, Approver);

        var (_, auditor) = await GetDataAttributesAsync(instanceId, Auditor);

        Assert.True(Has(auditor, "decisionNote"), $"decisionNote disappeared for the auditor: {auditor}");
        Assert.True(Has(auditor, "auditTrail"), $"auditTrail disappeared for the auditor: {auditor}");
    }

    /// <summary>
    /// Locates the instance-data object inside a view response without depending on the envelope's
    /// exact shape — <c>data</c>, <c>attributes</c>, or the root itself.
    /// </summary>
    private static JsonElement? FindInstanceData(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;

        foreach (var property in new[] { "data", "attributes" })
        {
            if (body.TryGetProperty(property, out var candidate) &&
                candidate.ValueKind == JsonValueKind.Object &&
                candidate.TryGetProperty("caseRef", out _))
            {
                return candidate;
            }
        }

        return body.TryGetProperty("caseRef", out _) ? body : null;
    }
}
