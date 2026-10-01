using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// Field exposure BELOW the root: <c>x-roles</c> allow and deny sets, <c>x-masking</c> and
/// <c>x-encryption</c> hash on nested objects, a guarded array of objects, a guarded non-string field and a
/// predefined (<c>$InstanceStarter</c>) grant. Everything is seeded in clear on the start transition.
/// <code>
/// customer                        (unguarded object)
///   name                          unguarded
///   tckn                          x-masking mask keepLast 3, allow: auditor
///   segment                       x-roles ALLOW approver, auditor
///   contact                       x-roles DENY maker            (subtree)
///     email                       x-masking mask keepFirst 2 keepLast 4
///     phone                       x-encryption hash (stored as a digest, no exemption)
///     address.city                unguarded
///     address.line1               x-roles ALLOW auditor
/// accounts[]                      x-roles ALLOW approver, auditor (array of objects)
/// riskScore                       x-roles ALLOW auditor  (number)
/// ownerNote                       x-roles ALLOW $InstanceStarter
/// tags[]                          unguarded array of strings
/// </code>
/// Each allow-list field is asserted for EVERY caller shape — granted, not granted, granted-plus-denied and
/// role-less — so a visibility regression in either direction fails a named test.
/// </summary>
public class SchemaFieldExposureNestedTests : RoleMatrixLabTestBase
{
    private const string Email = "ayse.yilmaz@example.com";
    private const string MaskedEmail = "ay*****************.com";
    private const string Phone = "+905321234567";
    private const string Tckn = "10000000146";
    private const string MaskedTckn = "********146";

    public SchemaFieldExposureNestedTests(VNextTestEnvironment environment) : base(environment) { }

    private const string DigestPrefix = "HASHED:SHA256:";

    private static bool TryPath(JsonElement body, string path, out JsonElement value)
    {
        value = body;
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }

        return true;
    }

    private static bool HasPath(JsonElement body, string path) => TryPath(body, path, out _);

    private static string? StrAt(JsonElement body, string path) =>
        TryPath(body, path, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<JsonElement> DataAsync(string instanceId, string? roles)
    {
        var (status, attributes) = await GetDataAttributesAsync(instanceId, roles);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(HasPath(attributes, "customer.name"), $"the unguarded nested control field is missing for {roles ?? "<no roles>"}: {attributes}");
        return attributes;
    }

    // ── x-roles ALLOW on a nested leaf ───────────────────────────────────────

    [Theory]
    [InlineData(Approver, true)]
    [InlineData(Auditor, true)]
    [InlineData(Maker, false)]
    [InlineData(Viewer, false)]
    [InlineData(null, false)]
    [InlineData($"{Maker},{Approver}", true)]
    [InlineData("morph-idm.aprover", false)]
    public async Task ANestedAllowListLeaf_IsVisibleExactlyToTheGrantedRoles(string? roles, bool visible)
    {
        var instanceId = await StartCaseAsync("nest-allow-leaf");

        var attributes = await DataAsync(instanceId, roles);

        Assert.Equal(visible, HasPath(attributes, "customer.segment"));
        if (visible)
            Assert.Equal("premium", StrAt(attributes, "customer.segment"));
    }

    // ── x-roles DENY on a nested subtree ─────────────────────────────────────

    [Theory]
    [InlineData(Approver, true)]
    [InlineData(Auditor, true)]
    [InlineData(Viewer, true)]
    [InlineData(Maker, false)]
    [InlineData($"{Maker},{Auditor}", false)]
    [InlineData(null, false)]
    public async Task ANestedDenySubtree_IsHiddenFromTheDeniedRole_AndFromARoleLessCaller(string? roles, bool visible)
    {
        var instanceId = await StartCaseAsync("nest-deny-subtree");

        var attributes = await DataAsync(instanceId, roles);

        Assert.Equal(visible, HasPath(attributes, "customer.contact"));
    }

    /// <summary>
    /// A hidden parent takes its whole subtree with it: none of the children's values — masked, hashed,
    /// clear or separately guarded — may appear anywhere in the body, in any form.
    /// </summary>
    [Fact]
    public async Task AHiddenSubtree_LeaksNothingOfItsChildren()
    {
        var instanceId = await StartCaseAsync("nest-deny-leak");

        var (_, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/data", headers: HeadersFor(Maker));

        foreach (var fragment in new[] { Email, MaskedEmail, Phone, "\"phone\"", "İstanbul", "Levent", "\"contact\"" })
            Assert.DoesNotContain(fragment, body);
    }

    // ── rules below a visible parent ─────────────────────────────────────────

    [Fact]
    public async Task InsideAVisibleSubtree_EachChildRuleAppliesOnItsOwn_ForTheApprover()
    {
        var instanceId = await StartCaseAsync("nest-children-approver");

        var attributes = await DataAsync(instanceId, Approver);

        Assert.Equal(MaskedEmail, StrAt(attributes, "customer.contact.email"));
        Assert.StartsWith(DigestPrefix, StrAt(attributes, "customer.contact.phone"));
        Assert.Equal("İstanbul", StrAt(attributes, "customer.contact.address.city"));
        Assert.False(HasPath(attributes, "customer.contact.address.line1"),
            "address.line1 is an auditor-only ALLOW leaf two levels below a visible parent");
        Assert.Equal(MaskedTckn, StrAt(attributes, "customer.tckn"));
        Assert.Equal("Ayşe Yılmaz", StrAt(attributes, "customer.name"));
    }

    [Fact]
    public async Task InsideAVisibleSubtree_TheAuditorIsExemptFromTheMask_ButNotFromAnUnexemptMaskOrTheHash()
    {
        var instanceId = await StartCaseAsync("nest-children-auditor");

        var attributes = await DataAsync(instanceId, Auditor);

        Assert.StartsWith(DigestPrefix, StrAt(attributes, "customer.contact.phone")); // hash: stored digest, no exemption
        Assert.Equal(Tckn, StrAt(attributes, "customer.tckn"));                   // mask, auditor exempt
        Assert.Equal(MaskedEmail, StrAt(attributes, "customer.contact.email"));  // mask, no exemption
        Assert.Equal("Levent Mah. Büyükdere Cad. No:1", StrAt(attributes, "customer.contact.address.line1"));
    }

    // ── guarded array of objects ─────────────────────────────────────────────

    [Theory]
    [InlineData(Approver, true)]
    [InlineData(Auditor, true)]
    [InlineData(Maker, false)]
    [InlineData(null, false)]
    public async Task AGuardedArrayOfObjects_IsServedWholeOrNotAtAll(string? roles, bool visible)
    {
        var instanceId = await StartCaseAsync("nest-array");

        var attributes = await DataAsync(instanceId, roles);

        Assert.Equal(visible, attributes.TryGetProperty("accounts", out var accounts));
        if (!visible)
            return;

        Assert.Equal(JsonValueKind.Array, accounts.ValueKind);
        Assert.Equal(2, accounts.GetArrayLength());
        Assert.Equal("TR560001000000000000000001", accounts[0].GetProperty("iban").GetString());
        Assert.Equal(JsonValueKind.Number, accounts[0].GetProperty("balance").ValueKind);
        Assert.Equal(1250.75m, accounts[0].GetProperty("balance").GetDecimal());
        Assert.Equal(0m, accounts[1].GetProperty("balance").GetDecimal());
    }

    // ── guarded non-string ───────────────────────────────────────────────────

    [Theory]
    [InlineData(Auditor, true)]
    [InlineData(Approver, false)]
    [InlineData(Maker, false)]
    [InlineData(null, false)]
    public async Task AGuardedNumber_KeepsItsTypeForTheGrantedRole_AndIsAbsentForEveryoneElse(string? roles, bool visible)
    {
        var instanceId = await StartCaseAsync("nest-number");

        var attributes = await DataAsync(instanceId, roles);

        Assert.Equal(visible, attributes.TryGetProperty("riskScore", out var score));
        if (visible)
        {
            Assert.Equal(JsonValueKind.Number, score.ValueKind);
            Assert.Equal(72, score.GetInt32());
        }
    }

    // ── unguarded array ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnguardedArrayOfStrings_IsServedVerbatimAndInOrder()
    {
        var instanceId = await StartCaseAsync("nest-tags");

        foreach (var roles in new[] { Maker, Approver, Auditor, (string?)null })
        {
            var attributes = await DataAsync(instanceId, roles);
            var tags = attributes.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray();
            Assert.Equal(new[] { "vip", "kyc-done", "tr" }, tags);
        }
    }

    // ── predefined grant ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>$InstanceStarter</c> matches the actor identity (<c>act_sub</c>), not a role. Started as
    /// <c>alice</c>: alice sees <c>ownerNote</c> whatever her roles; bob and an anonymous caller do not.
    /// </summary>
    [Fact]
    public async Task APredefinedStarterGrant_FollowsTheActorIdentity_NotTheRoles()
    {
        var start = HeadersFor(Approver);
        start["act_sub"] = "alice";
        var response = await Api.StartInstanceAsync(Workflow, new { caseRef = $"nest-starter-{Guid.NewGuid():N}"[..24] }, start);
        var instanceId = response.Body.GetProperty("id").GetString()!;
        await WaitUntilSettledAsync(Workflow, instanceId, Approver);

        async Task<bool> SeesOwnerNote(string? actor, string? roles)
        {
            var headers = HeadersFor(roles);
            if (actor is not null) headers["act_sub"] = actor;
            var (status, body) = await SendRawAsync(HttpMethod.Get,
                $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/data", headers: headers);
            Assert.Equal(HttpStatusCode.OK, status);
            return body.Contains("owner-only note", StringComparison.Ordinal);
        }

        Assert.True(await SeesOwnerNote("alice", Approver));
        Assert.True(await SeesOwnerNote("alice", Maker));
        Assert.True(await SeesOwnerNote("alice", null));
        Assert.False(await SeesOwnerNote("bob", Approver));
        Assert.False(await SeesOwnerNote(null, Auditor));
    }

    // ── one decision, every surface ──────────────────────────────────────────

    [Theory]
    [InlineData(Maker)]
    [InlineData(Approver)]
    [InlineData(Auditor)]
    public async Task TheInstanceGet_ServesExactlyTheSameNestedTreeAsTheDataFunction(string roles)
    {
        var instanceId = await StartCaseAsync("nest-surfaces");

        var data = await DataAsync(instanceId, roles);
        var instance = await Api.GetInstanceAsync(Workflow, instanceId, Headers(roles));
        var attributes = instance.Body.GetProperty("attributes");

        Assert.Equal(data.GetProperty("customer").GetRawText(), attributes.GetProperty("customer").GetRawText());
        Assert.Equal(data.TryGetProperty("accounts", out _), attributes.TryGetProperty("accounts", out _));
        Assert.Equal(data.TryGetProperty("riskScore", out _), attributes.TryGetProperty("riskScore", out _));
    }

    /// <summary>The sync transition response is the third caller-facing surface and applies the same plan.</summary>
    [Fact]
    public async Task ASyncTransitionResponse_AppliesTheSameNestedPlan()
    {
        var instanceId = await StartCaseAsync("nest-sync");

        var (status, raw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/record-note?sync=true",
            new { }, HeadersFor(Maker));
        Assert.True(status is HttpStatusCode.OK, $"record-note answered {(int)status}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var attributes = doc.RootElement.GetProperty("attributes");
        Assert.False(HasPath(attributes, "customer.contact"), "the maker is denied customer.contact");
        Assert.False(HasPath(attributes, "customer.segment"), "customer.segment is an approver/auditor allow list");
        Assert.Equal(MaskedTckn, StrAt(attributes, "customer.tckn"));
        Assert.DoesNotContain(Phone, raw);
        Assert.DoesNotContain(Email, raw);
    }

    // ── trigger-task reads follow the TASK'S credential ──────────────────────

    /// <summary>
    /// A header-less task read is its caller's: driven by the MAKER, from whom customer.contact is hidden, nothing is copied.
    /// </summary>
    [Fact]
    public async Task ATaskWithoutHeaders_ReadsAsItsCaller_CannotCopyOutOfASubtreeHiddenFromIt()
    {
        var instanceId = await StartCaseAsync("nest-task-maker");

        await RunAcceptedAsync(Workflow, instanceId, "mirror-self", new { }, Maker);
        await WaitUntilSettledAsync(Workflow, instanceId, Approver);
        await AssertNotFaultedAsync(Workflow, instanceId, Approver);

        Assert.Equal("<absent>", StrAt(await DataAsync(instanceId, Approver), "mirroredPhone"));
    }

    /// <summary>With an auditor credential the task sees customer.contact and copies the stored digest of the phone.</summary>
    [Fact]
    public async Task ATaskReadWithAnAuditorCredential_CopiesOutOfASubtreeItsCallerCannotSee()
    {
        var instanceId = await StartCaseAsync("nest-task-auditor");

        await RunAcceptedAsync(Workflow, instanceId, "mirror-self-auditor", new { }, Maker);
        await WaitUntilSettledAsync(Workflow, instanceId, Approver);
        await AssertNotFaultedAsync(Workflow, instanceId, Approver);

        var attributes = await DataAsync(instanceId, Approver);
        Assert.StartsWith(DigestPrefix, StrAt(attributes, "mirroredPhone"));
        Assert.Equal(StrAt(attributes, "customer.contact.phone"), StrAt(attributes, "mirroredPhone"));
    }
}
