using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Publish-validation rows V-1…V-11, V-13…V-20, P-26 (errors) and W-1, W-2a, W-2b, W-2c, W-4, W-5, W-6 (warnings)
/// of <c>attributes.rowSecurity</c> (vnext#1107). Every row publishes its own flow under a fresh key
/// <c>rs-pv-{row}-{suffix}</c>, built from the committed <c>rs-a</c> definition with only <c>rowSecurity</c> (and the
/// master schema, <c>rs-pv-schema</c>) replaced, so rows never collide with each other, with earlier runs or with
/// the other lab tasks.
/// <para>
/// <b>Error rows</b>: 400, <c>code</c> = <c>App:900006</c> (<c>WorkflowErrorCodes.InvalidWorkflow</c>), the RS-E code
/// is the prefix of one of the validation messages, and a follow-up lookup of the key in <c>sys-flows</c> answers
/// 404 — nothing was published. The schema-aware codes (RS-E08, E14, E15, E18) need the master schema at publish:
/// <c>rs-pv-schema</c> is a committed fixture (one field per protected-path class), loaded by <c>wf sync</c>.
/// </para>
/// <para>
/// <b>Warning rows</b>: warnings are log-only (R-F8). The row asserts 200, that the response carries no
/// <c>RS-W</c> text, and that the flow is live: <c>sys-flows</c> serves the key with its <c>rowSecurity</c>, and the
/// flow's instance list answers 200. The warning itself is checked in the runtime log by the controller (Task 12,
/// <c>ComponentValidationWarning</c> + the code); each row writes its key, code and UTC time to the test output.
/// </para>
/// <para>Provider-independent: publish validation never resolves a caller role, so these rows run in the D and M runs.</para>
/// </summary>
public class PublishValidationTests : RowSecurityLabTestBase
{
    /// <summary>Master schema with an x-roles, an encrypt, a hash and an x-masking path (fixture, Task 11D).</summary>
    private const string PvSchema = "rs-pv-schema";

    /// <summary><c>WorkflowErrorCodes.InvalidWorkflow</c>.</summary>
    private const string InvalidWorkflow = "App:900006";

    private const string H = "$.context.Headers.";
    private const string Q = "$.context.QueryParameters.";

    /// <summary>An unfiltered rule, so a warning-row flow is a normal opted-in flow (K1: no default).</summary>
    private const string HqRule = """{ "roles": ["hq-auditor"] }""";

    private readonly ITestOutputHelper _output;

    public PublishValidationTests(VNextTestEnvironment environment, ITestOutputHelper output) : base(environment)
    {
        _output = output;
    }

    // ── errors: roles ────────────────────────────────────────────────────────

    [Fact]
    public Task V01_PreviousUserAsRuleRole_IsRejected_RSE01() =>
        AssertRejectedAsync("v01", Rules("""{ "roles": ["$PreviousUser"] }"""), "RS-E01");

    [Fact]
    public Task V02_PreviousBehalfOfUserAsRuleRole_IsRejected_RSE01() =>
        AssertRejectedAsync("v02", Rules("""{ "roles": ["$PreviousBehalfOfUser"] }"""), "RS-E01");

    [Theory]
    [InlineData("role", "$role.branch-staff")]
    [InlineData("user", "$user.alice")]
    [InlineData("behalf", "$userBehalfOf.alice")]
    public Task V03_DynamicGrantAsRuleRole_IsRejected_RSE02(string variant, string role) =>
        AssertRejectedAsync($"v03{variant}", Rules($$"""{ "roles": ["{{role}}"] }"""), "RS-E02");

    /// <summary>
    /// A role-grant combinator object as a rule role. The doc and <c>RowSecurityErrorCodes.E03</c> name RS-E03; see
    /// the report — the runtime rejects it (400, nothing published) but with a JSON-conversion message, no RS code.
    /// </summary>
    [Theory]
    [InlineData("allof", """{ "allOf": ["branch-staff", "hq-auditor"] }""")]
    [InlineData("anyof", """{ "anyOf": ["branch-staff", "hq-auditor"] }""")]
    public Task V04_CombinatorAsRuleRole_IsRejected_RSE03(string variant, string combinator) =>
        AssertRejectedAsync($"v04{variant}", Rules($$"""{ "roles": [{{combinator}}] }"""), "RS-E03");

    [Theory]
    [InlineData("static", """["branch-staff", "$InstanceStarter"]""", "RS-E04")]
    [InlineData("twoidentities", """["$InstanceStarter", "$InstanceBehalfOfStarter"]""", "RS-E04")]
    [InlineData("otherreserved", """["$InstanceOwner"]""", "RS-E05")]
    public Task V05_StaticAndReservedMixed_OrOtherReserved_IsRejected(string variant, string roles, string code) =>
        AssertRejectedAsync($"v05{variant}", Rules($$"""{ "roles": {{roles}} }"""), code);

    // ── errors: filter shape and parameters ──────────────────────────────────

    [Theory]
    [InlineData("starter", "$InstanceStarter")]
    [InlineData("behalf", "$InstanceBehalfOfStarter")]
    public Task V06_StarterShorthandOnWrongColumn_IsRejected_RSE06(string variant, string shorthand) =>
        AssertRejectedAsync($"v06{variant}", Rules(Leaf("branchCode", "eq", Str(shorthand))), "RS-E06");

    /// <summary>
    /// Exact ordinal <c>$.context.</c> prefix, malformed (R-F7): empty name, unknown source, unknown CurrentUser field.
    /// The case-insensitive near miss (<c>$.Context.</c>) is the W-4 warning, not this error.
    /// </summary>
    [Theory]
    [InlineData("emptyname", H)]
    [InlineData("unknownsource", "$.context.Session.branch")]
    [InlineData("unknownuserfield", "$.context.CurrentUser.Email")]
    public Task V07_MalformedParameter_IsRejected_RSE07(string variant, string parameter) =>
        AssertRejectedAsync($"v07{variant}", Rules(Leaf("branchCode", "eq", Str(parameter))), "RS-E07");

    [Theory]
    [InlineData("leaf", "secret.pin", "eq", "\"x\"")]
    [InlineData("ancestor", "secret", "isNull", "false")]
    public Task V08_EncryptPathOrAncestor_IsRejected_RSE08(string variant, string field, string op, string operand) =>
        AssertRejectedAsync($"v08{variant}", Rules(Leaf(field, op, operand)), "RS-E08");

    [Theory]
    [InlineData("operator", """{ "attributes": { "branchCode": { "regex": "BR-.*" } } }""")]
    [InlineData("notobject", "[]")]
    [InlineData("booleanliteral", """{ "attributes": { "branchCode": { "eq": true } } }""")]
    public Task V09_InvalidOperatorOrJsonShape_IsRejected_RSE09(string variant, string filter) =>
        AssertRejectedAsync($"v09{variant}", Rules(Rule("branch-staff", filter)), "RS-E09");

    [Theory]
    [InlineData("three", """["a", "b", "c"]""")]
    [InlineData("one", """["a"]""")]
    public Task V10_BetweenArityNotTwo_IsRejected_RSE10(string variant, string operand) =>
        AssertRejectedAsync($"v10{variant}", Rules(Leaf("region", "between", operand)), "RS-E10");

    [Fact]
    public Task V11_SplitOnScalarOperator_IsRejected_RSE11() =>
        AssertRejectedAsync("v11",
            Rules(Leaf("branchCode", "eq", $$"""{ "$param": "{{H}}x-branch-code", "split": "," }""")), "RS-E11");

    [Theory]
    [InlineData("sort", "\"sort\": [\"branchCode\"]")]
    [InlineData("orderby", "\"orderBy\": \"branchCode\"")]
    [InlineData("groupby", "\"groupBy\": { \"fields\": [\"attributes.branchCode\"] }")]
    [InlineData("aggregations", "\"aggregations\": { \"count\": true }")]
    public Task V13_QueryShapeInsideRule_IsRejected_RSE13(string variant, string member) =>
        AssertRejectedAsync($"v13{variant}",
            Rules(Rule("branch-staff", $$"""{ "attributes": { "branchCode": { "eq": "BR-001" } }, {{member}} }""")),
            "RS-E13");

    // ── errors: schema- and key-aware ────────────────────────────────────────

    [Fact]
    public Task V14_HashPath_IsRejected_RSE14() =>
        AssertRejectedAsync("v14", Rules(Leaf("tckn", "eq", "\"12345678901\"")), "RS-E14");

    [Fact]
    public Task V15_MaskingPath_IsRejected_RSE15() =>
        AssertRejectedAsync("v15", Rules(Leaf("iban", "eq", Str(H + "x-iban"))), "RS-E15");

    [Fact]
    public Task V16_RowSecurityOnSysFlow_IsRejected_RSE16() =>
        AssertRejectedAsync("v16", Rules(HqRule), "RS-E16", keyPrefix: "sys-");

    /// <summary>Parameter on a pattern operator (like / match / endswith) or inside <c>includes</c> (SG-9).</summary>
    [Theory]
    [InlineData("like", "like", "branchCode")]
    [InlineData("match", "match", "branchCode")]
    [InlineData("endswith", "endswith", "branchCode")]
    [InlineData("includes", "includes", "region")]
    public Task V17_ParameterOnPatternOperatorOrInsideIncludes_IsRejected_RSE17(string variant, string op, string field)
    {
        var operand = op == "includes"
            ? $$"""{ "code": "{{H}}x-branch-code" }"""
            : Str(H + "x-branch-code");
        return AssertRejectedAsync($"v17{variant}", Rules(Leaf(field, op, operand)), "RS-E17");
    }

    /// <summary><c>$.context.Route.*</c> is an unknown source (R-F13); <c>$.context.Query.*</c> too (only <c>QueryParameters</c> is kept).</summary>
    [Theory]
    [InlineData("route", "$.context.Route.instanceId")]
    [InlineData("query", "$.context.Query.branch")]
    public Task V18_RouteOrQuerySource_IsRejected_RSE07(string variant, string parameter) =>
        AssertRejectedAsync($"v18{variant}", Rules(Leaf("branchCode", "eq", Str(parameter))), "RS-E07");

    /// <summary>Re-keyed by K1: a <c>default</c> member, or an empty <c>rules</c> array, is RS-E12.</summary>
    [Theory]
    [InlineData("default", """{ "default": "allow", "rules": [ { "roles": ["hq-auditor"] } ] }""")]
    [InlineData("emptyrules", """{ "rules": [] }""")]
    public Task V19_DefaultMemberOrEmptyRules_IsRejected_RSE12(string variant, string rowSecurity) =>
        AssertRejectedAsync($"v19{variant}", rowSecurity, "RS-E12");

    /// <summary>D1: a <c>QueryParameters</c> value compared with an <c>x-roles</c> path lets a caller probe a hidden field.</summary>
    [Fact]
    public Task V20_QueryParametersLeafOnXRolesPath_IsRejected_RSE18() =>
        AssertRejectedAsync("v20", Rules(Leaf("protected", "eq", Str(Q + "probe"))), "RS-E18");

    /// <summary>P-26 (resolved, SG-9): a parameter on <c>startswith</c> is a publish error, same family as V-17.</summary>
    [Fact]
    public Task P26_ParameterOnStartsWith_IsRejected_RSE17() =>
        AssertRejectedAsync("p26", Rules(Leaf("branchCode", "startswith", Str(Q + "prefix"))), "RS-E17");

    // ── warnings (log-only, R-F8) ────────────────────────────────────────────

    [Fact]
    public Task W01_FieldNotInMasterSchema_IsPublished_RSW1() =>
        AssertPublishedAsync("w01", Rules(HqRule, Leaf("ghostField", "eq", Str(H + "x-ghost"))), "RS-W1");

    [Fact]
    public Task W02a_InOnFieldWithoutXIndexed_IsPublished_RSW2a() =>
        AssertPublishedAsync("w02a", Rules(HqRule, Leaf("branchCode", "in", """["BR-001", "BR-002"]""")), "RS-W2a");

    [Fact]
    public Task W02b_IdentityRule_IsPublished_RSW2b() =>
        AssertPublishedAsync("w02b", Rules(HqRule, """{ "roles": ["$InstanceStarter"] }"""), "RS-W2b");

    [Fact]
    public Task W02c_NeAsOnlyPredicate_IsPublished_RSW2c() =>
        AssertPublishedAsync("w02c", Rules(HqRule, Leaf("kind", "ne", "\"archived\"")), "RS-W2c");

    /// <summary>R-F7: the case-insensitive near miss <c>$.Context.</c> is a literal plus RS-W4 (an exact prefix is V-7).</summary>
    [Fact]
    public Task W04_NearMissContextPrefix_IsPublishedAsLiteral_RSW4() =>
        AssertPublishedAsync("w04", Rules(HqRule, Leaf("branchCode", "eq", Str("$.Context.Headers.x-branch-code"))),
            "RS-W4");

    [Fact]
    public Task W05_NotOnNonRequiredField_IsPublished_RSW5() =>
        AssertPublishedAsync("w05",
            Rules(HqRule, Rule("branch-staff", """{ "not": { "attributes": { "branchCode": { "eq": "BR-999" } } } }""")),
            "RS-W5");

    /// <summary>W-6 split: a <c>Headers</c> value on an <c>x-roles</c> path is RS-W6r (a <c>QueryParameters</c> one is V-20).</summary>
    [Fact]
    public Task W06a_HeadersLeafOnXRolesPath_IsPublished_RSW6r() =>
        AssertPublishedAsync("w06a", Rules(HqRule, Leaf("protected", "eq", Str(H + "x-protected"))), "RS-W6r");

    /// <summary>W-6 split: any <c>QueryParameters</c> value on an unprotected path is RS-W6q.</summary>
    [Fact]
    public Task W06b_QueryParametersLeafOnUnprotectedPath_IsPublished_RSW6q() =>
        AssertPublishedAsync("w06b", Rules(HqRule, Leaf("branchCode", "eq", Str(Q + "branch"))), "RS-W6q");

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary><c>{"rules":[...]}</c> from rule JSON fragments.</summary>
    private static string Rules(params string[] rules) => $$"""{ "rules": [ {{string.Join(", ", rules)}} ] }""";

    private static string Rule(string role, string filter) =>
        $$"""{ "roles": ["{{role}}"], "filter": {{filter}} }""";

    /// <summary>A <c>branch-staff</c> rule with one attribute leaf <c>{field: {op: operand}}</c>.</summary>
    private static string Leaf(string field, string op, string operand) =>
        Rule("branch-staff", $$"""{ "attributes": { "{{field}}": { "{{op}}": {{operand}} } } }""");

    private static string Str(string value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// The row's flow: <c>rs-a</c> with key <c>{prefix}rs-pv-{row}-{suffix}</c>, master schema
    /// <see cref="PvSchema"/> and <paramref name="rowSecurity"/> verbatim.
    /// </summary>
    private static (string Key, JsonObject Definition) Definition(string row, string rowSecurity, string keyPrefix = "")
    {
        var key = $"{keyPrefix}rs-pv-{row}-{Guid.NewGuid().ToString("N")[..8]}";
        var definition = LoadWorkflow("rs-a");
        definition["key"] = key;
        definition["attributes"]!["labels"] = JsonNode.Parse(
            $$"""[{"language":"en-US","label":"Row Security Lab publish validation {{row}}"}]""");
        definition["attributes"]!["schema"]!["key"] = PvSchema;
        definition["attributes"]!["rowSecurity"] = JsonNode.Parse(rowSecurity);
        return (key, definition);
    }

    private async Task AssertRejectedAsync(string row, string rowSecurity, string code, string keyPrefix = "")
    {
        var (key, definition) = Definition(row, rowSecurity, keyPrefix);
        var (status, body) = await PublishAsync(definition);

        Assert.True(status == HttpStatusCode.BadRequest, $"{row} {key}: publish answered {(int)status}, want 400: {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("code", out var errorCode) && errorCode.GetString() == InvalidWorkflow,
            $"{row}: error code is not {InvalidWorkflow}: {body}");

        var messages = new List<string>();
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            foreach (var member in errors.EnumerateObject())
                foreach (var message in member.Value.EnumerateArray())
                    messages.Add(message.GetString() ?? string.Empty);

        Assert.True(messages.Any(m => m.StartsWith(code + ":", StringComparison.Ordinal)),
            $"{row}: no {code} among the validation errors: {body}");

        await AssertNotPublishedAsync(row, key);
    }

    private async Task AssertNotPublishedAsync(string row, string key)
    {
        var (status, body) = await SendRawJsonAsync(HttpMethod.Get, DefinitionUrl(key), "{}", Headers());
        Assert.True(status == HttpStatusCode.NotFound,
            $"{row}: the rejected key {key} is visible in sys-flows ({(int)status}): {body}");
    }

    private async Task AssertPublishedAsync(string row, string rowSecurity, string warning)
    {
        var (key, definition) = Definition(row, rowSecurity);
        var publishedAt = DateTimeOffset.UtcNow;
        var (status, body) = await PublishAsync(definition);

        Assert.True(status == HttpStatusCode.OK, $"{row} {key}: publish answered {(int)status}, want 200: {body}");
        Assert.DoesNotContain("RS-", body, StringComparison.Ordinal);

        _output.WriteLine($"W-ROW {row} code={warning} key={key} publishedAtUtc={publishedAt:O}");

        var (getStatus, getBody) = await SendRawJsonAsync(HttpMethod.Get, DefinitionUrl(key), "{}", Headers());
        Assert.True(getStatus == HttpStatusCode.OK, $"{row}: sys-flows lookup of {key} answered {(int)getStatus}: {getBody}");
        var attributes = JsonDocument.Parse(getBody).RootElement.GetProperty("attributes");
        Assert.True(attributes.TryGetProperty("rowSecurity", out var stored) &&
                    JsonNode.DeepEquals(JsonNode.Parse(stored.GetRawText()), JsonNode.Parse(rowSecurity)),
            $"{row}: sys-flows does not serve the published rowSecurity: {getBody}");

        var (listStatus, listBody) = await ListRawAsync(key, Hq(), RawNoTestId);
        Assert.True(listStatus == HttpStatusCode.OK, $"{row}: list of the new flow {key} answered {(int)listStatus}: {listBody}");
        Assert.Equal(0, JsonDocument.Parse(listBody).RootElement.GetProperty("items").GetArrayLength());
    }

    private static string DefinitionUrl(string key) => $"api/v1/{Domain}/workflows/sys-flows/instances/{key}";
}
