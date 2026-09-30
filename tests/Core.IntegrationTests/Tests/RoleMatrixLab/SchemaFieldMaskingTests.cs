using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// Master-schema <c>x-masking</c> — values transformed on the way out, after <c>x-roles</c> pruning.
/// <para>
/// The master schema carries three masked fields seeded in clear on the start transition:
/// <list type="bullet">
///   <item><c>maskedForAll</c> — <c>mask</c>, <c>keepLast: 4</c>, no roles: every caller sees it masked</item>
///   <item><c>maskedExceptAuditor</c> — <c>mask</c>, <c>keepLast: 3</c>, allow-only exemption for the auditor</item>
///   <item><c>replacedNote</c> — <c>replace</c> with <c>[gizli]</c></item>
/// </list>
/// Stored data never changes; the <c>mirror-self</c> transition proves it by copying the stored values
/// through a GetInstanceData task (a system read) into unguarded fields.
/// </para>
/// </summary>
public class SchemaFieldMaskingTests : RoleMatrixLabTestBase
{
    private const string Iban = "TR330006100519786457841326";
    private const string MaskedIban = "**********************1326";
    private const string Tckn = "12345678901";
    private const string MaskedTckn = "********901";
    private const string Placeholder = "[gizli]";

    /// <summary>Own client: the base's raw sender does not surface response headers, and the ETag is one.</summary>
    private readonly HttpClient _etagClient;

    public SchemaFieldMaskingTests(VNextTestEnvironment environment) : base(environment)
    {
        _etagClient = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    private static string? Str(JsonElement body, string property) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ── data function ────────────────────────────────────────────────────────

    [Fact]
    public async Task ARuleWithoutRoles_MasksTheValueForEveryCaller()
    {
        var instanceId = await StartCaseAsync("xmask-all");

        foreach (var role in new[] { Maker, Approver, Auditor })
        {
            var (status, attributes) = await GetDataAttributesAsync(instanceId, role);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(MaskedIban, Str(attributes, "maskedForAll"));
            Assert.Equal(Placeholder, Str(attributes, "replacedNote"));
            Assert.True(Has(attributes, "caseRef"), $"{role} lost the unguarded control field: {attributes}");
        }
    }

    [Fact]
    public async Task TheAllowListedRole_SeesTheValueInClear_EveryOtherCallerSeesItMasked()
    {
        var instanceId = await StartCaseAsync("xmask-exempt");

        var (_, auditor) = await GetDataAttributesAsync(instanceId, Auditor);
        Assert.Equal(Tckn, Str(auditor, "maskedExceptAuditor"));

        foreach (var roles in new[] { Maker, Approver, $"{Maker},{Approver}" })
        {
            var (_, attributes) = await GetDataAttributesAsync(instanceId, roles);
            Assert.Equal(MaskedTckn, Str(attributes, "maskedExceptAuditor"));
        }

        var (_, both) = await GetDataAttributesAsync(instanceId, $"{Approver},{Auditor}");
        Assert.Equal(Tckn, Str(both, "maskedExceptAuditor"));
    }

    /// <summary>
    /// The exemption list is allow-only and fails closed: a caller whose role simply does not match — a typo,
    /// or a role added to the identity provider later — must fall on the masked side.
    /// </summary>
    [Fact]
    public async Task AMisspelledRole_IsMasked()
    {
        var instanceId = await StartCaseAsync("xmask-typo");

        var (status, attributes) = await GetDataAttributesAsync(instanceId, $"{Approver},morph-idm.audtor");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(MaskedTckn, Str(attributes, "maskedExceptAuditor"));
    }

    // ── x-encryption: hash (applied on write) ────────────────────────────────

    private static void AssertDigest(string? value)
    {
        Assert.NotNull(value);
        Assert.StartsWith("HASHED:SHA256:", value);
        Assert.Equal("HASHED:SHA256:".Length + 64, value!.Length);
    }

    /// <summary>
    /// The funnel replaces the value with <c>HASHED:SHA256:&lt;hex&gt;</c> (HMAC under the instance's own salt) before
    /// it is stored, so every caller — the auditor included, since hash has no exemption list — gets the same digest
    /// and the raw value is on no surface at all.
    /// </summary>
    [Fact]
    public async Task AHashedField_IsStoredAsADigest_AndEveryCallerSeesTheSameDigest()
    {
        var instanceId = await StartCaseAsync("xenc-hash");

        var (_, maker) = await GetDataAttributesAsync(instanceId, Maker);
        var digest = Str(maker, "hashedCustomerNo");
        AssertDigest(digest);

        foreach (var roles in new[] { Approver, Auditor, "morph-idm.audtor" })
        {
            var (status, body) = await SendRawAsync(HttpMethod.Get,
                $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/data", headers: HeadersFor(roles));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain("98765432109", body);
            var (_, attributes) = await GetDataAttributesAsync(instanceId, roles);
            Assert.Equal(digest, Str(attributes, "hashedCustomerNo"));
        }

        var instance = await Api.GetInstanceAsync(Workflow, instanceId, Headers(Auditor));
        Assert.Equal(digest, Str(instance.Body.GetProperty("attributes"), "hashedCustomerNo"));
    }

    /// <summary>An unchanged value keeps its digest across later writes (it is not re-hashed).</summary>
    [Fact]
    public async Task TheDigest_IsStable_AcrossLaterWrites()
    {
        var instanceId = await StartCaseAsync("xenc-hash-carry");
        var (_, before) = await GetDataAttributesAsync(instanceId, Maker);

        var (status, raw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/record-note?sync=true",
            new { }, HeadersFor(Maker));
        Assert.True(status is HttpStatusCode.OK, $"record-note answered {(int)status}: {raw}");

        var (_, after) = await GetDataAttributesAsync(instanceId, Maker);
        Assert.Equal(Str(before, "hashedCustomerNo"), Str(after, "hashedCustomerNo"));
    }

    /// <summary>Per-instance salt: the same value hashes differently in two instances — digests cannot be matched.</summary>
    [Fact]
    public async Task TheSameValue_HashesDifferentlyInTwoInstances()
    {
        var first = await StartCaseAsync("xenc-match-a");
        var second = await StartCaseAsync("xenc-match-b");

        var (_, a) = await GetDataAttributesAsync(first, Approver);
        var (_, b) = await GetDataAttributesAsync(second, Approver);

        AssertDigest(Str(a, "hashedCustomerNo"));
        AssertDigest(Str(b, "hashedCustomerNo"));
        Assert.NotEqual(Str(a, "hashedCustomerNo"), Str(b, "hashedCustomerNo"));
    }

    // ── other read surfaces ──────────────────────────────────────────────────

    /// <summary>
    /// Instance GET and list serve data exactly as stored (committee decision; Phase 2 revisits): no masking, so the
    /// masked fields come back in clear, and the hashed field as its stored digest. The data function stays masked.
    /// </summary>
    [Fact]
    public async Task InstanceGetAndList_ServeTheStoredValues_TheDataFunctionStaysMasked()
    {
        var instanceId = await StartCaseAsync("xmask-surfaces");

        var instance = await Api.GetInstanceAsync(Workflow, instanceId, Headers(Approver));
        var attributes = instance.Body.GetProperty("attributes");
        Assert.Equal(Iban, Str(attributes, "maskedForAll"));
        Assert.Equal(Tckn, Str(attributes, "maskedExceptAuditor"));
        AssertDigest(Str(attributes, "hashedCustomerNo"));

        var (listStatus, listBody) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances?pageSize=100", headers: HeadersFor(Approver));
        Assert.Equal(HttpStatusCode.OK, listStatus);
        using var list = JsonDocument.Parse(listBody);
        var item = FindItem(list.RootElement, instanceId);
        Assert.True(item.HasValue, $"instance {instanceId} is not on the first list page");
        var listed = item!.Value.GetProperty("attributes");
        Assert.Equal(Iban, Str(listed, "maskedForAll"));
        Assert.Equal(Tckn, Str(listed, "maskedExceptAuditor"));
        Assert.NotEqual(Placeholder, Str(listed, "replacedNote"));

        var (_, data) = await GetDataAttributesAsync(instanceId, Approver);
        Assert.Equal(MaskedIban, Str(data, "maskedForAll"));
        Assert.Equal(MaskedTckn, Str(data, "maskedExceptAuditor"));
    }

    // ── cache / ETag ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ARepeatedRead_Answers304_AndNeverServesAnotherCallersBody()
    {
        var instanceId = await StartCaseAsync("xmask-etag");
        var url = $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/data";

        var (first, firstEtag) = await GetWithEtagAsync(url, Approver);
        Assert.Equal(HttpStatusCode.OK, first);
        Assert.False(string.IsNullOrEmpty(firstEtag), "the data function answered without an ETag");

        var (second, _) = await GetWithEtagAsync(url, Approver, firstEtag);
        Assert.Equal(HttpStatusCode.NotModified, second);

        // Same ETag, different caller scope: the auditor must get its own (clear) body, never a 304.
        var (auditor, _) = await GetWithEtagAsync(url, Auditor, firstEtag);
        Assert.Equal(HttpStatusCode.OK, auditor);
    }

    // ── system read: stored data is untouched ────────────────────────────────

    /// <summary>
    /// A GetInstanceData task reads under the engine's own identity. Driven by the MAKER — a caller
    /// that sees <c>maskedExceptAuditor</c> masked and does not see <c>auditTrail</c> at all — the copies
    /// must still hold the stored values. On the runtime before the SystemRead flag the auditTrail copy
    /// came back <c>&lt;absent&gt;</c>: the task read was x-roles-pruned under the caller's roles.
    /// </summary>
    [Fact]
    public async Task ATriggerTaskRead_CopiesTheStoredValues_WhateverTheCallerMaySee()
    {
        var instanceId = await StartCaseAsync("xmask-system");

        await RunAcceptedAsync(Workflow, instanceId, "mirror-self", new { }, Maker);
        await WaitUntilSettledAsync(Workflow, instanceId, Approver);
        await AssertNotFaultedAsync(Workflow, instanceId, Approver);

        var (_, attributes) = await GetDataAttributesAsync(instanceId, Approver);
        Assert.Equal(Tckn, Str(attributes, "mirroredMasked"));
        Assert.Equal("seeded-audit-trail", Str(attributes, "mirroredAuditTrail"));
    }

    [Fact]
    public async Task ThePublicDataFunction_CannotBeTalkedIntoASystemRead()
    {
        var instanceId = await StartCaseAsync("xmask-forge");
        var headers = HeadersFor(Approver);
        headers["X-System-Read"] = "true";
        headers["systemRead"] = "true";

        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/data?systemRead=true",
            headers: headers);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(Iban, body);
        Assert.DoesNotContain(Tckn, body);
    }

    private async Task<(HttpStatusCode Status, string? ETag)> GetWithEtagAsync(string url, string roles, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in Headers(roles)) request.Headers.TryAddWithoutValidation(key, value);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        using var response = await _etagClient.SendAsync(request);
        return (response.StatusCode, response.Headers.ETag?.ToString());
    }

    /// <summary>Depth-first search for the list item whose <c>id</c> is <paramref name="instanceId"/>.</summary>
    private static JsonElement? FindItem(JsonElement node, string instanceId)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), instanceId, StringComparison.OrdinalIgnoreCase) &&
                node.TryGetProperty("attributes", out _))
                return node.Clone();
            foreach (var property in node.EnumerateObject())
                if (FindItem(property.Value, instanceId) is { } hit) return hit;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in node.EnumerateArray())
                if (FindItem(element, instanceId) is { } hit) return hit;
        }

        return null;
    }
}
