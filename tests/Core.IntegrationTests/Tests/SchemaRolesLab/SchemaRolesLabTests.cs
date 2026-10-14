using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SchemaRolesLab;

/// <summary>
/// schema-roles-lab: the runtime side of the vnext-schema role vocabularies (vnext-schema #153 revision).
/// <list type="bullet">
/// <item><c>x-computed</c> with <c>evaluatedBy: "runtime"</c> is computed on every read surface (instance GET,
/// list, data function, sync transition output), from what the CALLER sees (after <c>x-roles</c>), and is never
/// stored; <c>evaluatedBy: "client"</c> is left to clients.</item>
/// <item><c>x-encryption</c> with <c>source: "client"</c> is accepted on the master and on a transition schema and
/// never handled — stored and served as sent, filterable; <c>source: "runtime"</c> next to it keeps the at-rest
/// behaviour (token for a non-exempt caller, plaintext for the exempt one).</item>
/// <item>Publish rules: <c>x-filterOperators</c> query tokens, <c>evaluatedBy: "server"</c> and a runtime computed
/// field on a transition schema are refused; a legacy <c>attributes.type</c> still publishes.</item>
/// </list>
/// </summary>
public class SchemaRolesLabTests : WorkflowTestBase
{
    private const string Workflow = "schema-roles-lab";
    private const string Admin = "srl.admin";
    private const string TokenPrefix = "ENCRYPTED:AES256:i1:";

    public SchemaRolesLabTests(VNextTestEnvironment environment) : base(environment) { }

    // ── x-computed ──────────────────────────────────────────────────────────

    /// <summary>The same computed values arrive on instance GET, the data function, the list and a sync transition.</summary>
    [Fact]
    public async Task RuntimeComputed_IsAddedOnEveryReadSurface()
    {
        var testId = TestId("surfaces");
        var instanceId = await StartLabAsync(new { testId, kind = "person", firstName = "Ada", lastName = "Lovelace", a = 10, b = 4 });

        var get = (await Api.GetInstanceAsync(Workflow, instanceId, Headers(Admin))).Body.GetProperty("attributes");
        AssertComputed(get, "Ada Lovelace", 14m, 2.5m);
        Assert.False(get.TryGetProperty("nickname", out _), "evaluatedBy 'client' must be left to clients");

        AssertComputed(await DataAsync(instanceId, Admin), "Ada Lovelace", 14m, 2.5m);

        var filter = Uri.EscapeDataString("{\"attributes\":{\"testId\":{\"eq\":\"" + testId + "\"}}}");
        var (listStatus, listBody) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances?filter={filter}&pageSize=10", headers: Headers(Admin));
        Assert.True(listStatus == HttpStatusCode.OK, $"list answered {(int)listStatus}: {listBody}");
        using (var list = JsonDocument.Parse(listBody))
        {
            var item = FindItem(list.RootElement, instanceId);
            Assert.True(item.HasValue, $"instance {instanceId} is not on the filtered list: {listBody}");
            AssertComputed(item!.Value.GetProperty("attributes"), "Ada Lovelace", 14m, 2.5m);
        }

        var (syncStatus, syncRaw) = await SendRawAsync(new HttpMethod("PATCH"),
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/srl-update?sync=true",
            new { a = 20 }, Headers(Admin));
        Assert.True(syncStatus == HttpStatusCode.OK, $"srl-update answered {(int)syncStatus}: {syncRaw}");
        using var sync = JsonDocument.Parse(syncRaw);
        AssertComputed(sync.RootElement.GetProperty("attributes"), "Ada Lovelace", 24m, 5m);
    }

    /// <summary>A computed value is built from the caller's view: a caller who cannot see lastName gets it without.</summary>
    [Fact]
    public async Task RuntimeComputed_IsBuiltFromTheCallersView()
    {
        var instanceId = await StartLabAsync(new { testId = TestId("view"), kind = "person", firstName = "Ada", lastName = "Lovelace" });

        var asAdmin = await DataAsync(instanceId, Admin);
        Assert.Equal("Ada Lovelace", asAdmin.GetProperty("fullName").GetString());

        var asNobody = await DataAsync(instanceId, null);
        Assert.False(asNobody.TryGetProperty("lastName", out _), "x-roles should prune lastName for a role-less caller");
        Assert.Equal("Ada", asNobody.GetProperty("fullName").GetString());
    }

    /// <summary>The first case whose condition holds wins; an update re-evaluates on the next read.</summary>
    [Fact]
    public async Task RuntimeComputed_FirstMatchingCaseWins_AndFollowsTheData()
    {
        var instanceId = await StartLabAsync(new { testId = TestId("cases"), kind = "person", firstName = "Ada", lastName = "Lovelace", a = 1, b = 1 });
        Assert.Equal("Ada Lovelace", (await DataAsync(instanceId, Admin)).GetProperty("fullName").GetString());

        await RunAcceptedAsync(Workflow, instanceId, "srl-update", new { kind = "corporate", title = "Acme A.Ş.", a = 5 }, Admin);
        await WaitUntilAsync(async () =>
                (await DataAsync(instanceId, Admin)).GetProperty("fullName").GetString() == "Acme A.Ş.",
            "fullName follows the corporate case after the update");

        var data = await DataAsync(instanceId, Admin);
        Assert.Equal(6m, data.GetProperty("total").GetDecimal());
    }

    /// <summary>An unresolvable value follows whenAbsent: '' by default, null when declared.</summary>
    [Fact]
    public async Task RuntimeComputed_Unresolvable_FollowsWhenAbsent()
    {
        var instanceId = await StartLabAsync(new { testId = TestId("absent"), kind = "person", a = 3, b = 0 });

        var data = await DataAsync(instanceId, Admin);
        Assert.Equal(string.Empty, data.GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("ratio").ValueKind);
        Assert.Equal(3m, data.GetProperty("total").GetDecimal());
    }

    /// <summary>A nested computed field is written into its parent object, and never creates a missing parent.</summary>
    [Fact]
    public async Task RuntimeComputed_NestedField_IsWrittenIntoItsParentOnly()
    {
        var withAddress = await StartLabAsync(new { testId = TestId("nested"), address = new { city = "İstanbul", district = "Kadıköy" } });
        Assert.Equal("Kadıköy, İstanbul",
            (await DataAsync(withAddress, Admin)).GetProperty("address").GetProperty("label").GetString());

        var withoutAddress = await StartLabAsync(new { testId = TestId("no-parent") });
        Assert.False((await DataAsync(withoutAddress, Admin)).TryGetProperty("address", out _));
    }

    /// <summary>A value the client sends for a computed field is dropped on write: the read always shows the computed one.</summary>
    [Fact]
    public async Task RuntimeComputed_AValueSentByTheClient_IsNeverStored()
    {
        var instanceId = await StartLabAsync(new
        {
            testId = TestId("echo"), kind = "person", firstName = "Ada", lastName = "Lovelace", a = 1, b = 2,
            fullName = "stale-from-start", total = 999
        });
        var afterStart = await DataAsync(instanceId, Admin);
        Assert.Equal("Ada Lovelace", afterStart.GetProperty("fullName").GetString());
        Assert.Equal(3m, afterStart.GetProperty("total").GetDecimal());

        // The transition schema itself declares fullName (a plain field there); the master drops it on write.
        await RunAcceptedAsync(Workflow, instanceId, "srl-update", new { fullName = "stale-from-transition" }, Admin);
        await WaitUntilSettledAsync(Workflow, instanceId, Admin);
        Assert.Equal("Ada Lovelace", (await DataAsync(instanceId, Admin)).GetProperty("fullName").GetString());
    }

    // ── x-encryption ────────────────────────────────────────────────────────

    /// <summary>
    /// On the master, a client rule is accepted and never handled — stored and served as sent, to every caller,
    /// and filterable — while a runtime rule on the same schema still encrypts at rest.
    /// </summary>
    [Fact]
    public async Task ClientEncryption_OnTheMaster_IsNotHandled_RuntimeEncryption_IsUnchanged()
    {
        const string Sealed = "BGpk.c2VhbGVkLWJ5LXRoZS1jbGllbnQ";
        var testId = TestId("enc");
        var instanceId = await StartLabAsync(new { testId, pin = Sealed, secret = "s3cr3t" });

        var asNobody = await DataAsync(instanceId, null);
        Assert.Equal(Sealed, asNobody.GetProperty("pin").GetString());
        Assert.StartsWith(TokenPrefix, asNobody.GetProperty("secret").GetString());

        var asAdmin = await DataAsync(instanceId, Admin);
        Assert.Equal(Sealed, asAdmin.GetProperty("pin").GetString());
        Assert.Equal("s3cr3t", asAdmin.GetProperty("secret").GetString());

        // A runtime-encrypted path refuses filters; a client one is a plain stored value and filters normally.
        var filter = Uri.EscapeDataString("{\"attributes\":{\"pin\":{\"eq\":\"" + Sealed + "\"}}}");
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances?filter={filter}&pageSize=10", headers: Headers(Admin));
        Assert.True(status == HttpStatusCode.OK, $"a filter on the client-sealed pin answered {(int)status}: {body}");
        using var list = JsonDocument.Parse(body);
        Assert.True(FindItem(list.RootElement, instanceId).HasValue, $"instance {instanceId} not found by pin: {body}");
    }

    /// <summary>A transition schema carrying a client rule publishes and its body is stored as the client sent it.</summary>
    [Fact]
    public async Task ClientEncryption_OnATransitionSchema_IsAccepted_AndStoredAsSent()
    {
        const string Sealed = "eyJjaXBoZXJ0ZXh0IjoiYWJjIn0";
        var instanceId = await StartLabAsync(new { testId = TestId("enc-transition") });

        await RunAcceptedAsync(Workflow, instanceId, "srl-update", new { passwordEnc = Sealed }, Admin);
        await WaitUntilSettledAsync(Workflow, instanceId, Admin);

        Assert.Equal(Sealed, (await DataAsync(instanceId, null)).GetProperty("passwordEnc").GetString());
    }

    // ── publish rules ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("ge", "declare 'gte'")]
    [InlineData("like", "declare 'contains'")]
    [InlineData("foo", "is not an operator")]
    public async Task Publish_RefusesFilterOperatorsTheQueryCouldNeverMatch(string op, string expected)
    {
        var schema = ProbeSchema("master", new JsonObject
        {
            ["amount"] = new JsonObject { ["type"] = "number", ["x-filterOperators"] = new JsonArray(op) }
        });

        var (status, body) = await PublishAsync(schema);

        Assert.True(status == HttpStatusCode.BadRequest, $"x-filterOperators '{op}' answered {(int)status}: {body}");
        Assert.Contains(expected, body);
    }

    [Fact]
    public async Task Publish_RefusesServer_AndARuntimeComputedOnATransitionSchema()
    {
        JsonObject Computed(string by) => new()
        {
            ["a"] = new JsonObject { ["type"] = "number" },
            ["b"] = new JsonObject { ["type"] = "number" },
            ["t"] = new JsonObject
            {
                ["type"] = "number",
                ["x-computed"] = new JsonObject
                {
                    ["evaluatedBy"] = by,
                    ["fallback"] = new JsonObject { ["arithmetic"] = new JsonObject { ["op"] = "add", ["fields"] = new JsonArray("a", "b") } }
                }
            }
        };

        var (serverStatus, serverBody) = await PublishAsync(ProbeSchema("master", Computed("server")));
        Assert.True(serverStatus == HttpStatusCode.BadRequest, $"evaluatedBy 'server' answered {(int)serverStatus}: {serverBody}");
        Assert.Contains("use 'runtime'", serverBody);

        var (transitionStatus, transitionBody) = await PublishAsync(ProbeSchema("transition", Computed("runtime")));
        Assert.True(transitionStatus == HttpStatusCode.BadRequest, $"runtime computed on a transition answered {(int)transitionStatus}: {transitionBody}");
        Assert.Contains("use evaluatedBy 'client'", transitionBody);

        var (masterStatus, masterBody) = await PublishAsync(ProbeSchema("master", Computed("runtime")));
        Assert.True(masterStatus is HttpStatusCode.OK or HttpStatusCode.Created, $"runtime computed on a master answered {(int)masterStatus}: {masterBody}");
    }

    [Theory]
    [InlineData("master")]
    [InlineData("transition")]
    [InlineData("workflow")]
    [InlineData(null)]
    public async Task Publish_AcceptsClientEncryption_OnAnySchema_AndALegacyOrMissingType(string? type)
    {
        var schema = ProbeSchema(type, new JsonObject
        {
            ["pin"] = new JsonObject
            {
                ["type"] = "string",
                ["x-encryption"] = new JsonObject
                {
                    ["source"] = "client",
                    ["key"] = "urn:client:context:read:device:memory:device.hpke.publicKey",
                    ["algorithm"] = "HPKE-P256-SHA256-AES128GCM"
                }
            }
        });

        var (status, body) = await PublishAsync(schema);

        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created, $"type '{type}' answered {(int)status}: {body}");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string TestId(string tag) => $"srl-{tag}-{Guid.NewGuid():N}"[..24];

    private async Task<string> StartLabAsync(object body)
    {
        var instanceId = await StartAsync(Workflow, body, Admin);
        await WaitUntilSettledAsync(Workflow, instanceId, Admin);
        await AssertNotFaultedAsync(Workflow, instanceId, Admin);
        return instanceId;
    }

    /// <summary>The data function's attributes (envelope <c>{ data: { … } }</c>).</summary>
    private async Task<JsonElement> DataAsync(string instanceId, string? roles)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/data", headers: Headers(roles));
        Assert.True(status == HttpStatusCode.OK, $"data function answered {(int)status}: {raw}");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d
            : root.TryGetProperty("attributes", out var a) ? a : root;
        return data.Clone();
    }

    private static void AssertComputed(JsonElement attributes, string fullName, decimal total, decimal ratio)
    {
        Assert.Equal(fullName, attributes.GetProperty("fullName").GetString());
        Assert.Equal(total, attributes.GetProperty("total").GetDecimal());
        Assert.Equal(ratio, attributes.GetProperty("ratio").GetDecimal());
    }

    private static JsonElement? FindItem(JsonElement node, string id)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String &&
                string.Equals(v.GetString(), id, StringComparison.OrdinalIgnoreCase) && node.TryGetProperty("attributes", out _))
                return node.Clone();
            foreach (var p in node.EnumerateObject())
                if (FindItem(p.Value, id) is { } hit) return hit;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in node.EnumerateArray())
                if (FindItem(e, id) is { } hit) return hit;
        }

        return null;
    }

    /// <summary>A fresh schema component (unique version: publish is version-immutable).</summary>
    private static JsonObject ProbeSchema(string? type, JsonObject properties)
    {
        var attributes = new JsonObject
        {
            ["schema"] = new JsonObject { ["type"] = "object", ["properties"] = properties }
        };
        if (type is not null) attributes["type"] = type;

        return new JsonObject
        {
            ["key"] = "schema-roles-lab-probe",
            ["version"] = $"9.{DateTime.UtcNow:yyMMdd}.{Random.Shared.Next(1, int.MaxValue)}",
            ["domain"] = "core",
            ["flow"] = "sys-schemas",
            ["flowVersion"] = "1.0.0",
            ["tags"] = new JsonArray("integration-test", "schema-roles-lab"),
            ["attributes"] = attributes
        };
    }

    private Task<(HttpStatusCode Status, string Body)> PublishAsync(JsonObject definition) =>
        SendRawJsonAsync(HttpMethod.Post, "api/v1/definitions/publish", definition.ToJsonString(), Headers());
}
