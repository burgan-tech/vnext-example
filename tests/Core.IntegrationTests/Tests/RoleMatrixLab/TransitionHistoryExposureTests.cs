using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// <c>GET …/instances/{id}/transitions</c> — the transition history — under the master schema's field rules (vnext#1260).
/// <para>
/// Each history row carries the request body exactly as it was recorded: plaintext, never filtered at write time. Before
/// #1260 the endpoint served that body to any caller, so a field the master schema prunes (<c>x-roles</c>), masks
/// (<c>x-masking</c>) or protects (<c>x-encryption</c>) on every other read surface leaked through the history. The body
/// now passes the same exposure plan as instance GET, list and the data function, built once per request for the caller.
/// </para>
/// <para>
/// The case is started with the guarded fields IN THE START BODY, so the start row's recorded body carries them in clear.
/// The seeding start task writes the same fields into instance data, but that is irrelevant here: every assertion reads
/// the history row, not the instance.
/// </para>
/// <para>
/// Header filtering (<c>Workflow:TransitionHistory:Headers:Read</c>) is configuration: with both lists empty — the
/// default this suite runs with — every recorded header is returned as before, which is what the last test pins.
/// </para>
/// </summary>
public class TransitionHistoryExposureTests : RoleMatrixLabTestBase
{
    private const string AuditNote = "history-audit-trail";
    private const string DecisionText = "history-decision-note";
    private const string Iban = "TR330006100519786457841326";
    private const string MaskedIban = "**********************1326";
    private const string Tckn = "12345678901";
    private const string MaskedTckn = "********901";
    private const string Placeholder = "[gizli]";
    private const string CustomerNo = "98765432109";
    private const string VaultEmail = "history-vault@example.com";
    private const string ProbeHeader = "x-history-probe";

    public TransitionHistoryExposureTests(VNextTestEnvironment environment) : base(environment) { }

    // ── body: x-roles ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>auditTrail</c> is allow-only for the auditor and <c>decisionNote</c> denies the maker. The maker's view of the
    /// start row loses both; the auditor's keeps both; the unguarded <c>caseRef</c> survives for everyone.
    /// </summary>
    [Fact]
    public async Task XRoles_PruneTheRecordedBody_PerCaller()
    {
        var instanceId = await StartWithGuardedBodyAsync("hist-xroles");

        var maker = await StartRowBodyAsync(instanceId, Maker);
        Assert.False(Has(maker, "auditTrail"), $"maker saw auditTrail in history: {maker}");
        Assert.False(Has(maker, "decisionNote"), $"maker saw decisionNote in history: {maker}");
        Assert.True(Has(maker, "caseRef"), $"maker lost the unguarded caseRef: {maker}");

        var auditor = await StartRowBodyAsync(instanceId, Auditor);
        Assert.Equal(AuditNote, Str(auditor, "auditTrail"));
        Assert.Equal(DecisionText, Str(auditor, "decisionNote"));

        var approver = await StartRowBodyAsync(instanceId, Approver);
        Assert.False(Has(approver, "auditTrail"), $"approver saw the auditor-only auditTrail: {approver}");
        Assert.Equal(DecisionText, Str(approver, "decisionNote"));
    }

    // ── body: x-masking ──────────────────────────────────────────────────────

    /// <summary>Masks apply to the recorded body as they do to the data function, exemption list included.</summary>
    [Fact]
    public async Task XMasking_AppliesToTheRecordedBody_WithTheAllowListExemption()
    {
        var instanceId = await StartWithGuardedBodyAsync("hist-xmask");

        var maker = await StartRowBodyAsync(instanceId, Maker);
        Assert.Equal(MaskedIban, Str(maker, "maskedForAll"));
        Assert.Equal(MaskedTckn, Str(maker, "maskedExceptAuditor"));
        Assert.Equal(Placeholder, Str(maker, "replacedNote"));

        var auditor = await StartRowBodyAsync(instanceId, Auditor);
        Assert.Equal(MaskedIban, Str(auditor, "maskedForAll"));
        Assert.Equal(Tckn, Str(auditor, "maskedExceptAuditor"));
    }

    // ── body: x-encryption ───────────────────────────────────────────────────

    /// <summary>
    /// The recorded body is plaintext, so there is no stored token or digest to serve. A <c>hash</c> field's raw value and
    /// a non-exempt caller's view of an <c>encrypt</c> field are masked; the <c>encrypt</c> allow role (auditor) reads it
    /// in clear, as on the data function.
    /// </summary>
    [Fact]
    public async Task XEncryption_NeverEchoesThePlaintextToANonExemptCaller()
    {
        var instanceId = await StartWithGuardedBodyAsync("hist-xenc");

        var (status, raw) = await HistoryRawAsync(instanceId, Maker);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(CustomerNo, raw);
        Assert.DoesNotContain(VaultEmail, raw);

        foreach (var roles in new[] { Approver, Auditor })
        {
            var body = await StartRowBodyAsync(instanceId, roles);
            Assert.NotEqual(CustomerNo, Str(body, "hashedCustomerNo"));
        }

        var auditorVault = (await StartRowBodyAsync(instanceId, Auditor)).GetProperty("vault");
        Assert.Equal(VaultEmail, Str(auditorVault, "email"));
    }

    /// <summary>
    /// A later row is filtered the same way: <c>record-note</c> (a <c>$self</c> shared transition without its own schema)
    /// carries guarded fields in its request body, and the maker's history view of that row is pruned and masked.
    /// </summary>
    [Fact]
    public async Task ALaterTransitionRow_IsFilteredWithTheSamePlan()
    {
        var instanceId = await StartCaseAsync("hist-later");

        var (status, raw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/record-note?sync=true",
            new { auditTrail = AuditNote, maskedForAll = Iban }, HeadersFor(Maker));
        Assert.True(status is HttpStatusCode.OK, $"record-note answered {(int)status}: {raw}");

        var row = await RowBodyAsync(instanceId, Maker, "record-note");
        Assert.False(Has(row, "auditTrail"), $"maker saw auditTrail on the record-note row: {row}");
        Assert.Equal(MaskedIban, Str(row, "maskedForAll"));

        var auditorRow = await RowBodyAsync(instanceId, Auditor, "record-note");
        Assert.Equal(AuditNote, Str(auditorRow, "auditTrail"));
    }

    // ── headers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// With no <c>Workflow:TransitionHistory:Headers:Read</c> configuration the recorded headers are returned as before —
    /// the change is opt-in per environment and does not move the response for a domain that sets nothing.
    /// </summary>
    [Fact]
    public async Task WithoutHeaderConfiguration_RecordedHeadersAreReturnedAsBefore()
    {
        var headers = HeadersFor(Approver);
        headers[ProbeHeader] = "probe-value";
        var start = await Api.StartInstanceAsync(Workflow,
            new { caseRef = $"hist-hdr-{Guid.NewGuid():N}"[..24] }, headers);
        var instanceId = start.Body.GetProperty("id").GetString()!;
        await WaitUntilSettledAsync(Workflow, instanceId, Approver);

        var row = await StartRowAsync(instanceId, Approver);
        Assert.True(row.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object,
            $"start row carries no header object: {row}");
        Assert.Equal("probe-value", Str(header, ProbeHeader));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<string> StartWithGuardedBodyAsync(string tag)
    {
        var body = new
        {
            caseRef = $"{tag}-{Guid.NewGuid():N}"[..24],
            auditTrail = AuditNote,
            decisionNote = DecisionText,
            maskedForAll = Iban,
            maskedExceptAuditor = Tckn,
            replacedNote = "history-replaced",
            hashedCustomerNo = CustomerNo,
            vault = new { email = VaultEmail, label = "history" },
        };

        var instanceId = await StartAsync(Workflow, body, Approver);
        await WaitUntilSettledAsync(Workflow, instanceId, Approver);
        await AssertNotFaultedAsync(Workflow, instanceId, Approver);
        return instanceId;
    }

    private Task<(HttpStatusCode Status, string Raw)> HistoryRawAsync(string instanceId, string? roles) =>
        SendRawAsync(HttpMethod.Get, $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions",
            headers: HeadersFor(roles));

    private async Task<JsonElement> RowAsync(string instanceId, string? roles, Func<JsonElement, bool> match, string what)
    {
        var (status, raw) = await HistoryRawAsync(instanceId, roles);
        Assert.True(status == HttpStatusCode.OK, $"history answered {(int)status}: {raw}");

        foreach (var row in JsonDocument.Parse(raw).RootElement.GetProperty("transitions").EnumerateArray())
        {
            if (match(row)) return row.Clone();
        }

        Assert.Fail($"history holds no {what} row: {raw}");
        return default;
    }

    private Task<JsonElement> StartRowAsync(string instanceId, string? roles) =>
        RowAsync(instanceId, roles, r => r.GetProperty("transitionId").GetString() == "start-role-matrix", "start");

    private async Task<JsonElement> StartRowBodyAsync(string instanceId, string? roles) =>
        BodyOf(await StartRowAsync(instanceId, roles));

    private async Task<JsonElement> RowBodyAsync(string instanceId, string? roles, string transitionKey) =>
        BodyOf(await RowAsync(instanceId, roles, r => r.GetProperty("transitionId").GetString() == transitionKey, transitionKey));

    private static JsonElement BodyOf(JsonElement row)
    {
        Assert.True(row.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object,
            $"history row carries no body object: {row}");
        return body;
    }

    private static string? Str(JsonElement body, string property) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
