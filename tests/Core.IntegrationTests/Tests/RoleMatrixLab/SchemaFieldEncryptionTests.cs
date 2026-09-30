using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// Master-schema <c>x-encryption.type: "encrypt"</c> — AES-256-GCM of instance data at rest.
/// <para>
/// The start transition seeds <c>vault.email</c> (encrypt, allow-only exemption for the auditor), <c>vault.pin</c>
/// (encrypt, no exemption) and <c>vault.label</c> (plain) in clear. The runtime stores the two encrypted values as
/// <c>ENCRYPTED:AES256:i1:…</c> tokens under the instance's own key (generated on the first write, kept in the flow
/// schema's <c>InstanceSecrets</c> table); the engine keeps seeing plaintext. On the data function and the sync
/// response the auditor gets the plaintext of <c>vault.email</c> and every other caller the stored token; instance
/// GET and list serve the stored token to everyone. The column contents and the secret rows are checked outside the
/// test (psql) — see the README.
/// </para>
/// </summary>
public class SchemaFieldEncryptionTests : RoleMatrixLabTestBase
{
    private const string VaultEmail = "vault-user@example.com";
    private const string VaultPin = "4321";
    private const string TokenPrefix = "ENCRYPTED:AES256:i1:";

    public SchemaFieldEncryptionTests(VNextTestEnvironment environment) : base(environment)
    {
    }

    private static string? StrAt(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private async Task<string?> VaultEmailFor(string instanceId, string? roles)
    {
        var (status, attributes) = await GetDataAttributesAsync(instanceId, roles);
        Assert.Equal(HttpStatusCode.OK, status);
        return StrAt(attributes, "vault.email");
    }

    // ── read surfaces ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheExemptRole_ReadsThePlaintext_EveryOtherCallerTheStoredToken()
    {
        var instanceId = await StartCaseAsync("xenc-read");

        var (_, auditor) = await GetDataAttributesAsync(instanceId, Auditor);
        Assert.Equal(VaultEmail, StrAt(auditor, "vault.email"));
        Assert.StartsWith(TokenPrefix, StrAt(auditor, "vault.pin")); // pin has no exemption list
        Assert.Equal("not encrypted", StrAt(auditor, "vault.label"));

        foreach (var roles in new[] { Maker, Approver, $"{Maker},{Approver}", "morph-idm.audtor" })
        {
            var (status, attributes) = await GetDataAttributesAsync(instanceId, roles);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.StartsWith(TokenPrefix, StrAt(attributes, "vault.email"));
            Assert.StartsWith(TokenPrefix, StrAt(attributes, "vault.pin"));
            Assert.Equal("not encrypted", StrAt(attributes, "vault.label"));
        }

        var (_, both) = await GetDataAttributesAsync(instanceId, $"{Approver},{Auditor}");
        Assert.Equal(VaultEmail, StrAt(both, "vault.email"));
    }

    [Fact]
    public async Task ARoleLessCaller_GetsTheToken_NeverThePlaintext()
    {
        var instanceId = await StartCaseAsync("xenc-norole");

        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/data", headers: HeadersFor(null));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(VaultEmail, body);
        Assert.DoesNotContain("\"4321\"", body);
        Assert.Contains(TokenPrefix, body);
    }

    [Fact]
    public async Task InstanceGetAndList_ServeTheStoredToken_TheSyncResponseAppliesTheExemption()
    {
        var instanceId = await StartCaseAsync("xenc-surfaces");

        // GET and list serve data as stored: the token, even to the allow-listed auditor.
        foreach (var roles in new[] { Approver, Auditor })
        {
            var instance = await Api.GetInstanceAsync(Workflow, instanceId, Headers(roles));
            Assert.StartsWith(TokenPrefix, StrAt(instance.Body.GetProperty("attributes"), "vault.email"));
        }

        var (listStatus, listBody) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances?pageSize=100", headers: HeadersFor(Approver));
        Assert.Equal(HttpStatusCode.OK, listStatus);
        ListVaultEmailIsAToken(listBody, instanceId);

        var (syncStatus, raw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/record-note?sync=true",
            new { }, HeadersFor(Maker));
        Assert.True(syncStatus is HttpStatusCode.OK, $"record-note answered {(int)syncStatus}: {raw}");
        using var doc = JsonDocument.Parse(raw);
        Assert.StartsWith(TokenPrefix, StrAt(doc.RootElement.GetProperty("attributes"), "vault.email"));

        var (auditorStatus, auditorRaw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/record-note?sync=true",
            new { }, HeadersFor(Auditor));
        Assert.True(auditorStatus is HttpStatusCode.OK, $"record-note answered {(int)auditorStatus}: {auditorRaw}");
        using var auditorDoc = JsonDocument.Parse(auditorRaw);
        Assert.Equal(VaultEmail, StrAt(auditorDoc.RootElement.GetProperty("attributes"), "vault.email"));
    }

    /// <summary>The list item's own vault.email only (other instances may carry mirrored plaintext copies on purpose).</summary>
    private static void ListVaultEmailIsAToken(string listBody, string instanceId)
    {
        using var doc = JsonDocument.Parse(listBody);
        var item = Find(doc.RootElement, instanceId);
        Assert.True(item.HasValue, $"instance {instanceId} is not on the first list page");
        var email = StrAt(item!.Value.GetProperty("attributes"), "vault.email");
        Assert.StartsWith(TokenPrefix, email);

        static JsonElement? Find(JsonElement node, string id)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String &&
                    string.Equals(v.GetString(), id, StringComparison.OrdinalIgnoreCase) && node.TryGetProperty("attributes", out _))
                    return node.Clone();
                foreach (var p in node.EnumerateObject())
                    if (Find(p.Value, id) is { } hit) return hit;
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in node.EnumerateArray())
                    if (Find(e, id) is { } hit) return hit;
            }

            return null;
        }
    }

    // ── engine sees plaintext ────────────────────────────────────────────────

    /// <summary>
    /// The mirror-self task reads the instance as a system read and its mapping also reads its own script context:
    /// both must see the plaintext. (The copies land in unguarded fields — a documented consequence of copying.)
    /// </summary>
    [Fact]
    public async Task TheEngine_ScriptsAndSystemReads_SeeThePlaintext()
    {
        var instanceId = await StartCaseAsync("xenc-engine");

        await RunAcceptedAsync(Workflow, instanceId, "mirror-self", new { }, Maker);
        await WaitUntilSettledAsync(Workflow, instanceId, Approver);
        await AssertNotFaultedAsync(Workflow, instanceId, Approver);

        var (_, attributes) = await GetDataAttributesAsync(instanceId, Approver);
        Assert.Equal(VaultEmail, StrAt(attributes, "mirroredVaultEmail"));
        Assert.Equal(VaultEmail, StrAt(attributes, "scriptSawVaultEmail"));
        Assert.StartsWith(TokenPrefix, StrAt(attributes, "vault.email"));
    }

    /// <summary>
    /// Every append is a full copy; an unchanged encrypted value keeps its token (no fresh nonce), and the value's
    /// schema format (email) keeps validating although the stored form is a token.
    /// </summary>
    [Fact]
    public async Task AnUnchangedValue_KeepsItsToken_AcrossLaterWrites()
    {
        var instanceId = await StartCaseAsync("xenc-carry");
        var before = await VaultEmailFor(instanceId, Maker);

        var (status, raw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/record-note?sync=true",
            new { }, HeadersFor(Maker));
        Assert.True(status is HttpStatusCode.OK, $"record-note answered {(int)status}: {raw}");

        Assert.Equal(before, await VaultEmailFor(instanceId, Maker));
        Assert.Equal(VaultEmail, await VaultEmailFor(instanceId, Auditor));
    }

    // ── write rules ──────────────────────────────────────────────────────────

    /// <summary>A client that read the token and posts it back unchanged: accepted, nothing changes.</summary>
    [Fact]
    public async Task EchoingTheStoredToken_IsANoOp()
    {
        var instanceId = await StartCaseAsync("xenc-echo");
        var token = await VaultEmailFor(instanceId, Maker);

        var (status, raw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/submit-for-review?sync=true",
            new { vault = new { email = token } }, HeadersFor(Maker));

        Assert.True(status is HttpStatusCode.OK, $"submit-for-review answered {(int)status}: {raw}");
        await AssertNotFaultedAsync(Workflow, instanceId, Approver);
        Assert.Equal(VaultEmail, await VaultEmailFor(instanceId, Auditor));
        Assert.Equal(token, await VaultEmailFor(instanceId, Maker));
    }

    /// <summary>
    /// A token lifted from another instance (a non-exempt reader can see it) must not be planted here: it would
    /// decrypt for this instance's exempt reader only if the binding were missing. The runtime refuses it.
    /// </summary>
    [Fact]
    public async Task AForeignToken_IsRefused()
    {
        var victim = await StartCaseAsync("xenc-victim");
        var foreign = await VaultEmailFor(victim, Maker);
        var instanceId = await StartCaseAsync("xenc-attacker");

        var (status, raw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/submit-for-review?sync=true",
            new { vault = new { email = foreign } }, HeadersFor(Maker));

        Assert.True(status is HttpStatusCode.BadRequest, $"a foreign token answered {(int)status}: {raw}");
        Assert.Contains("Instance:100041", raw);
        // Refused before admission: the instance is untouched — not faulted, still in intake.
        await AssertNotFaultedAsync(Workflow, instanceId, Approver);
        Assert.Equal("intake", (await GetInstanceStateAsync(Workflow, instanceId, Approver)).State);
        Assert.Equal(VaultEmail, await VaultEmailFor(instanceId, Auditor));
    }

    [Fact]
    public async Task APlantedPrefix_OnAPlainField_IsRefused()
    {
        var instanceId = await StartCaseAsync("xenc-plant");

        var (status, raw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/submit-for-review?sync=true",
            new { vault = new { label = $"{TokenPrefix}AAAA" } }, HeadersFor(Maker));

        Assert.True(status is HttpStatusCode.BadRequest, $"a planted prefix answered {(int)status}: {raw}");
        Assert.Contains("Instance:100041", raw);
        await AssertNotFaultedAsync(Workflow, instanceId, Approver);
    }

    // ── queries ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The database holds ciphertext on the encrypted path: a filter is refused rather than silently matching
    /// nothing — whatever the host's EnforceMasterSchemaFiltering says (it is off on the local host).
    /// </summary>
    [Fact]
    public async Task AFilterOnAnEncryptedPath_IsRefused()
    {
        await StartCaseAsync("xenc-filter");
        var filter = Uri.EscapeDataString("""{"attributes":{"vault.email":{"eq":"vault-user@example.com"}}}""");

        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances?filter={filter}&pageSize=10", headers: HeadersFor(Approver));

        Assert.True(status is HttpStatusCode.BadRequest, $"a filter on vault.email answered {(int)status}: {raw}");
    }

    [Fact]
    public async Task AFilterOnTheEncryptedFieldsParent_IsRefused()
    {
        await StartCaseAsync("xenc-filter-parent");
        var filter = Uri.EscapeDataString("""{"attributes":{"vault":{"eq":{"email":"vault-user@example.com"}}}}""");

        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances?filter={filter}&pageSize=10", headers: HeadersFor(Approver));

        Assert.True(status is HttpStatusCode.BadRequest, $"a filter on vault answered {(int)status}: {raw}");
    }
}
