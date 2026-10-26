using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Shared plumbing for the <c>row-security-lab</c> suite (vnext#1107, DECISION-2026-10-23-row-security):
/// <c>attributes.rowSecurity</c> filters the instance list and the single GET (and the Get* trigger tasks that go
/// through them).
/// <para>
/// Every read here goes out raw with EXACTLY the caller headers a <see cref="Caller"/> describes — no
/// <c>user_reference</c>, no role unless asked. The SDK client and <see cref="WorkflowTestBase.Headers"/> always send a
/// complete set, and on a row-secured flow the identity and the role set ARE the inputs under test.
/// </para>
/// <para>
/// Population (<see cref="SeedAsync"/>): seven instances per test, one fresh <c>testId</c>, seeded and verified as an
/// <c>hq-auditor</c> (every opted-in lab flow has an unfiltered <c>hq-auditor</c> rule; with no <c>default</c> a flow
/// is deny by default, K1). "Own (BR-001)" is evaluated on LATEST data: <c>{r1, r2, r5}</c> (r5 starts BR-003 and is
/// moved to BR-001 by updateData; R-F2).
/// </para>
/// </summary>
public abstract class RowSecurityLabTestBase : WorkflowTestBase
{
    /// <summary>The domain every lab flow lives in (except partner <c>rs-xd-target</c>).</summary>
    protected const string Domain = "core";

    /// <summary>Pass as <c>callerFilter</c> to <see cref="ListIdsAsync"/> to send no filter at all (not even testId).</summary>
    protected const string RawNoTestId = "__raw-no-testid__";

    /// <summary>The role that every opted-in lab flow opens without a filter; used to seed and verify.</summary>
    protected const string HqAuditor = "hq-auditor";

    /// <summary>Actor (act_sub) of the seed rows that are not tied to a lab identity.</summary>
    protected const string Seeder = "rs-seeder";

    /// <summary>
    /// Skip reason of the D-run rows (<c>Skip.IfNot(ProviderIs("default"), DefaultProviderOnly)</c>): under morph-idm
    /// the role set comes from the IDM, not from the <c>role</c> header these rows send.
    /// </summary>
    protected const string DefaultProviderOnly =
        "D run: the role header is the role set only under the default caller-role provider";

    /// <summary>The Definition A/B rule header: <c>x-branch-code: {code}</c>.</summary>
    protected static Dictionary<string, string> Branch(string code) => new() { ["x-branch-code"] = code };

    /// <summary>The seven rows of one test. <c>TestId</c> isolates them from every other run.</summary>
    protected sealed record Population(string TestId, Guid R1, Guid R2, Guid R3, Guid R4, Guid R5, Guid R6, Guid R7)
    {
        /// <summary>All seven ids.</summary>
        public IReadOnlyList<Guid> All => [R1, R2, R3, R4, R5, R6, R7];
    }

    /// <summary>
    /// A caller as the runtime sees it. Only the headers set here are sent (plus request-id / device / token noise).
    /// <c>Sub</c> → <c>sub</c> (UserName, <c>$InstanceBehalfOfStarter</c>, morph-idm key), <c>ActSub</c> →
    /// <c>act_sub</c> (ActorUserName, <c>$InstanceStarter</c>), <c>Role</c> → <c>role</c> (the role set under the
    /// default provider; under morph-idm a present header replaces the IDM answer). <c>Query</c> is appended to the
    /// URL as is (<c>branch=BR-001&amp;branch=BR-002</c>).
    /// </summary>
    protected sealed record Caller(
        string? Sub = null,
        string? ActSub = null,
        string? Role = null,
        IDictionary<string, string>? Headers = null,
        string? Query = null);

    private readonly HttpClient _http;
    private readonly Dictionary<string, Population> _populations = new(StringComparer.Ordinal);

    /// <summary>The orchestrator base address every lab request goes to (for raw-socket requests).</summary>
    protected Uri OrchestratorUri => _http.BaseAddress!;

    /// <summary>The <c>sys-version-registry</c> pointer client (E-7: <c>set-default</c> switches the rule set, K2).</summary>
    protected VersionRegistryClient Registry { get; }

    protected RowSecurityLabTestBase(VNextTestEnvironment environment) : base(environment)
    {
        _http = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
        Registry = new VersionRegistryClient(environment.OrchestratorBaseUrl, () => Headers());
    }

    /// <summary>
    /// The caller-role provider this run's runtime uses: <c>VNEXT_CALLER_ROLE_PROVIDER</c>, default <c>default</c>.
    /// The runtime itself is switched with <c>CallerRoleProvider__Provider=morph-idm</c>; this only tells the suite.
    /// </summary>
    protected static bool ProviderIs(string provider) =>
        string.Equals(System.Environment.GetEnvironmentVariable("VNEXT_CALLER_ROLE_PROVIDER") is { Length: > 0 } value
                ? value
                : "default",
            provider, StringComparison.OrdinalIgnoreCase);

    // ── seeding ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the population on <paramref name="flow"/> and asserts that an <c>hq-auditor</c> lists exactly those
    /// seven ids:
    /// <list type="table">
    /// <item><term>r1</term><description>BR-001 · act_sub alice · amount 100 · north · owner alice · POS-A</description></item>
    /// <item><term>r2</term><description>BR-001 · act_sub carol · amount 200 · north · owner carol · POS-B</description></item>
    /// <item><term>r3</term><description>BR-002 · act_sub alice / sub bob · amount 300 · south · owner alice · POS-A</description></item>
    /// <item><term>r4</term><description>BR-002 · act_sub carol · amount 400 · south · owner carol · POS-B · <c>rs-close</c> ⇒ status C</description></item>
    /// <item><term>r5</term><description>BR-003 then updateData → BR-001 · amount 500 · east · owner dave · POS-C</description></item>
    /// <item><term>r6</term><description>BR-999 · amount 10 · west · owner erin · POS-C</description></item>
    /// <item><term>r7</term><description>no branchCode · amount 700 · north · owner frank · POS-A</description></item>
    /// </list>
    /// r5..r7 are started by <see cref="Seeder"/>. <paramref name="version"/> starts on that flow version
    /// (<c>?version=</c>, e.g. <c>rs-versioned</c> 1.0.0).
    /// </summary>
    protected async Task<Population> SeedAsync(string flow, string? version = null)
    {
        var testId = $"rs-{Guid.NewGuid():N}"[..20];

        var r1 = await StartRowAsync(flow, version, "alice", null, testId, "BR-001", 100, "north", "alice", "POS-A");
        var r2 = await StartRowAsync(flow, version, "carol", null, testId, "BR-001", 200, "north", "carol", "POS-B");
        var r3 = await StartRowAsync(flow, version, "alice", "bob", testId, "BR-002", 300, "south", "alice", "POS-A");
        var r4 = await StartRowAsync(flow, version, "carol", null, testId, "BR-002", 400, "south", "carol", "POS-B");
        var r5 = await StartRowAsync(flow, version, Seeder, null, testId, "BR-003", 500, "east", "dave", "POS-C");
        var r6 = await StartRowAsync(flow, version, Seeder, null, testId, "BR-999", 10, "west", "erin", "POS-C");
        var r7 = await StartRowAsync(flow, version, Seeder, null, testId, null, 700, "north", "frank", "POS-A");

        await SeedTransitionAsync(flow, r4, "rs-close", new { });
        await SeedTransitionAsync(flow, r5, "rs-update", new { attributes = new { branchCode = "BR-001" } });

        var population = new Population(testId, r1, r2, r3, r4, r5, r6, r7);
        _populations[flow] = population;

        var listed = await ListIdsAsync(flow, Hq());
        Assert.True(population.All.ToHashSet().SetEquals(listed),
            $"seed check: hq-auditor listed {listed.Count} of the 7 seeded {flow} rows ({string.Join(",", listed)})");
        return population;
    }

    /// <summary>An <c>hq-auditor</c> caller (identity <see cref="Seeder"/>).</summary>
    protected static Caller Hq() => new(Sub: Seeder, ActSub: Seeder, Role: HqAuditor);

    /// <summary>
    /// Makes <paramref name="population"/> the one <see cref="ListIdsAsync"/> scopes <paramref name="flow"/> to, for a
    /// test that seeded the same flow twice (e.g. <c>rs-versioned</c> on 1.0.0 and on 1.0.1).
    /// </summary>
    protected void UsePopulation(string flow, Population population) => _populations[flow] = population;

    private async Task<Guid> StartRowAsync(
        string flow, string? version, string actSub, string? sub, string testId, string? branchCode,
        decimal amount, string region, string owner, string positionCode)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["testId"] = testId,
            ["amount"] = amount,
            ["region"] = region,
            ["owner"] = owner,
            ["positionCode"] = positionCode,
        };
        if (branchCode is not null) attributes["branchCode"] = branchCode;

        var url = $"api/v1/{Domain}/workflows/{flow}/instances/start?sync=true" +
                  (version is null ? "" : $"&version={Uri.EscapeDataString(version)}");
        var caller = new Caller(Sub: sub, ActSub: actSub, Role: HqAuditor);
        var (status, body, _) = await SendAsync(HttpMethod.Post, url, caller, JsonContent.Create(new { attributes }));
        Assert.True((int)status < 400, $"seed start on {flow} refused with {(int)status}: {body}");

        var root = JsonDocument.Parse(body).RootElement;
        Assert.True(root.TryGetProperty("status", out var instanceStatus) && instanceStatus.GetString() == "A",
            $"seed start on {flow} did not rest Active: {body}");
        return Guid.Parse(root.GetProperty("id").GetString()!);
    }

    private async Task SeedTransitionAsync(string flow, Guid id, string transition, object body)
    {
        var url = $"api/v1/{Domain}/workflows/{flow}/instances/{id}/transitions/{transition}?sync=true";
        var (status, response, _) = await SendAsync(HttpMethod.Patch, url, Hq(), JsonContent.Create(body));
        Assert.True((int)status < 400, $"seed '{transition}' on {flow}/{id} refused with {(int)status}: {response}");
    }

    // ── reads ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists <paramref name="flow"/> as <paramref name="caller"/> and returns the ids. The population's
    /// <c>testId eq</c> is always ANDed in (the last <see cref="SeedAsync"/> of this flow in this test), together with
    /// <paramref name="callerFilter"/> (filter JSON) when given; <see cref="RawNoTestId"/> sends no filter at all.
    /// Fails on any status other than 200.
    /// </summary>
    protected async Task<IReadOnlyList<Guid>> ListIdsAsync(string flow, Caller caller, string? callerFilter = null)
    {
        var (status, body) = await ListRawAsync(flow, caller, callerFilter);
        Assert.True(status == HttpStatusCode.OK, $"list {flow} answered {(int)status}: {body}");

        var ids = new List<Guid>();
        foreach (var item in JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray())
            ids.Add(Guid.Parse(item.GetProperty("id").GetString()!));
        return ids;
    }

    /// <summary>
    /// The list call behind <see cref="ListIdsAsync"/>, status and body untouched (groupBy, errors, paging).
    /// <paramref name="callerFilter"/> is composed exactly as in <see cref="ListIdsAsync"/>.
    /// </summary>
    protected async Task<(HttpStatusCode Status, string Body)> ListRawAsync(
        string flow, Caller caller, string? callerFilter = null, string? extraQuery = null, int pageSize = 100)
    {
        return await ListCoreAsync(flow, caller, ComposeFilter(flow, callerFilter), extraQuery, pageSize);
    }

    /// <summary>
    /// Lists <paramref name="flow"/> through the request envelope (<c>{"filter": …, "groupBy": …}</c>, the GraphQL
    /// list route): the filter is composed as in <see cref="ListIdsAsync"/>, <paramref name="groupByJson"/> is the
    /// <c>groupBy</c> member verbatim (e.g. <c>{"fields":["attributes.branchCode"],"aggregations":{"count":true}}</c>).
    /// </summary>
    protected Task<(HttpStatusCode Status, string Body)> ListGroupedAsync(
        string flow, Caller caller, string groupByJson, string? callerFilter = null) =>
        ListCoreAsync(flow, caller, GroupEnvelope(flow, groupByJson, callerFilter), null, 100);

    /// <summary>The envelope <see cref="ListGroupedAsync"/> sends (also handed to the GetInstances task).</summary>
    protected string GroupEnvelope(string flow, string groupByJson, string? callerFilter = null) =>
        $"{{\"filter\":{ComposeFilter(flow, callerFilter)},\"groupBy\":{groupByJson}}}";

    private async Task<(HttpStatusCode Status, string Body)> ListCoreAsync(
        string flow, Caller caller, string? filter, string? extraQuery, int pageSize)
    {
        var query = new List<string> { $"pageSize={pageSize}" };
        if (filter is not null) query.Add("filter=" + Uri.EscapeDataString(filter));
        if (!string.IsNullOrEmpty(extraQuery)) query.Add(extraQuery);

        var url = $"api/v1/{Domain}/workflows/{flow}/instances?{string.Join("&", query)}";
        var (status, body, _) = await SendAsync(HttpMethod.Get, url, caller);
        return (status, body);
    }

    /// <summary>
    /// Single instance GET by id or key as <paramref name="caller"/>. Returns status, raw body and the <c>ETag</c>
    /// response header (null when absent). <paramref name="ifNoneMatch"/> makes it a conditional GET.
    /// </summary>
    protected async Task<(HttpStatusCode Status, string Body, string? ETag)> GetAsync(
        string flow, string idOrKey, Caller caller, string? ifNoneMatch = null)
    {
        var url = $"api/v1/{Domain}/workflows/{flow}/instances/{Uri.EscapeDataString(idOrKey)}";
        var extra = ifNoneMatch is null ? null : new Dictionary<string, string> { ["If-None-Match"] = ifNoneMatch };
        return await SendAsync(HttpMethod.Get, url, caller, extraHeaders: extra);
    }

    /// <summary>
    /// Asserts one L+G row: the list equals <paramref name="expected"/> exactly, every expected id answers 200 on
    /// the GET and every other population id answers 404.
    /// </summary>
    protected async Task AssertVisibleAsync(string flow, Population population, Caller caller, params Guid[] expected)
    {
        var listed = await ListIdsAsync(flow, caller);
        Assert.True(expected.ToHashSet().SetEquals(listed),
            $"list {flow}: expected [{Names(population, expected)}], got [{Names(population, listed)}]");

        foreach (var id in population.All)
        {
            var (status, body, _) = await GetAsync(flow, id.ToString(), caller);
            var want = expected.Contains(id) ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            Assert.True(status == want,
                $"GET {flow}/{Names(population, [id])} answered {(int)status}, expected {(int)want}: {body}");
        }
    }

    /// <summary>
    /// Sends one request as <paramref name="caller"/> (only its headers, plus <paramref name="extraHeaders"/>) and
    /// returns status, body and every response header (case-insensitive). <paramref name="url"/> is relative to the
    /// orchestrator (<c>api/v1/…</c>).
    /// </summary>
    protected async Task<(HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string> Headers)> SendAsCallerAsync(
        HttpMethod method, string url, Caller caller, object? body = null, IDictionary<string, string>? extraHeaders = null)
    {
        var (status, response, _, headers) = await SendCoreAsync(
            method, url, caller, body is null ? null : JsonContent.Create(body), extraHeaders);
        return (status, response, headers);
    }

    /// <summary>
    /// Runs one <c>rs-reader</c> transition (<c>read-list</c> / <c>read-one</c> / <c>trigger-one</c> /
    /// <c>read-data</c>) as <paramref name="caller"/> on a fresh reader instance and returns the reader's result object
    /// (<c>readerList</c> / <c>readerGet</c> / <c>readerTrigger</c> / <c>readerData</c>), read back as an <c>hq-auditor</c>
    /// (<c>rs-reader</c> has no <c>rowSecurity</c>). <paramref name="body"/> is the transition body
    /// (<c>targetFlow</c>, <c>filter</c> or <c>testId</c>, <c>headers</c>, …, see README § rs-reader).
    /// </summary>
    protected async Task<JsonElement> ReadThroughReaderAsync(Caller caller, string transition, object body)
    {
        const string reader = "rs-reader";
        var (status, started, _) = await SendAsync(HttpMethod.Post,
            $"api/v1/{Domain}/workflows/{reader}/instances/start?sync=true", caller,
            JsonContent.Create(new { attributes = new { purpose = transition } }));
        Assert.True((int)status < 400, $"{reader} start refused with {(int)status}: {started}");
        var id = JsonDocument.Parse(started).RootElement.GetProperty("id").GetString()!;

        var (ran, ranBody, _) = await SendAsync(HttpMethod.Patch,
            $"api/v1/{Domain}/workflows/{reader}/instances/{id}/transitions/{transition}?sync=true", caller,
            JsonContent.Create(body));
        Assert.True((int)ran < 400, $"{reader} '{transition}' refused with {(int)ran}: {ranBody}");

        var (read, data, _) = await GetAsync(reader, id, Hq());
        Assert.True(read == HttpStatusCode.OK, $"{reader}/{id} read back {(int)read}: {data}");
        var result = transition switch
        {
            "read-list" => "readerList",
            "read-one" => "readerGet",
            "read-data" => "readerData",
            _ => "readerTrigger",
        };
        var attributes = JsonDocument.Parse(data).RootElement.GetProperty("attributes");
        Assert.True(attributes.TryGetProperty(result, out var value), $"{reader}/{id} carries no {result}: {data}");
        return value.Clone();
    }

    /// <summary>Publishes a definition verbatim (<c>api/v1/definitions/publish</c>), as HistoryNoneLabTests does.</summary>
    protected Task<(HttpStatusCode Status, string Body)> PublishAsync(JsonObject definition) =>
        SendRawJsonAsync(HttpMethod.Post, "api/v1/definitions/publish", definition.ToJsonString(), Headers());

    /// <summary>Reads a lab fixture (<c>core/Workflows/row-security-lab/{key}.json</c>) as a mutable node.</summary>
    protected static JsonObject LoadWorkflow(string key)
    {
        var path = Path.Combine(RepoRoot(), "core", "Workflows", "row-security-lab", $"{key}.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    /// <summary>The <c>rs-versioned</c> version whose rule reads <c>region</c> instead of <c>branchCode</c>.</summary>
    protected const string VersionedV101 = "1.0.1";

    /// <summary>
    /// Publishes <c>rs-versioned</c> 1.0.1, built from the committed 1.0.0 file with exactly two rules:
    /// <c>hq-auditor</c> (no filter) and <c>branch-staff → region eq $.context.Headers.x-region</c>. The single source
    /// of that definition, so every test publishes the same bytes. Idempotent: a 409 "same version already exists"
    /// (<c>Instance:100002</c>) counts as published.
    /// <para>
    /// Once published, 1.0.1 is "latest" for <c>rs-versioned</c> on this runtime for every later run; 1.0.0's rule is
    /// in force only while the <c>sys-version-registry</c> pointer pins 1.0.0. Tests set or clear the pointer explicitly.
    /// </para>
    /// </summary>
    protected async Task PublishVersionedV101Async()
    {
        var definition = LoadWorkflow("rs-versioned");
        definition["version"] = VersionedV101;
        definition["attributes"]!["labels"] = JsonNode.Parse(
            """[{"language":"en-US","label":"Row Security Lab versioned (1.0.1 region rule)"}]""");
        definition["attributes"]!["rowSecurity"] = JsonNode.Parse("""
            {
              "rules": [
                { "roles": ["hq-auditor"] },
                { "roles": ["branch-staff"],
                  "filter": { "attributes": { "region": { "eq": "$.context.Headers.x-region" } } } }
              ]
            }
            """);

        var (status, body) = await PublishAsync(definition);
        var alreadyThere = status == HttpStatusCode.Conflict && body.Contains("100002", StringComparison.Ordinal);
        Assert.True((int)status < 300 || alreadyThere, $"publishing rs-versioned {VersionedV101} answered {(int)status}: {body}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>"r1,r3" for ids of the population, the raw id otherwise — failure messages name rows.</summary>
    protected static string Names(Population population, IEnumerable<Guid> ids)
    {
        var names = new Dictionary<Guid, string>
        {
            [population.R1] = "r1", [population.R2] = "r2", [population.R3] = "r3", [population.R4] = "r4",
            [population.R5] = "r5", [population.R6] = "r6", [population.R7] = "r7",
        };
        return string.Join(",", ids.Select(id => names.TryGetValue(id, out var name) ? name : id.ToString()));
    }

    /// <summary>
    /// The <c>filter</c> query value <see cref="ListIdsAsync"/> sends: the population's <c>testId eq</c>, ANDed with
    /// <paramref name="callerFilter"/> when given; null for <see cref="RawNoTestId"/>.
    /// </summary>
    protected string? ComposeFilter(string flow, string? callerFilter)
    {
        if (callerFilter == RawNoTestId) return null;
        if (!_populations.TryGetValue(flow, out var population))
            throw new InvalidOperationException($"ListIdsAsync({flow}) before SeedAsync({flow}) in this test");

        var testIdFilter = JsonSerializer.Serialize(new { attributes = new { testId = new { eq = population.TestId } } });
        return callerFilter is null ? testIdFilter : $"{{\"and\":[{testIdFilter},{callerFilter}]}}";
    }

    private async Task<(HttpStatusCode Status, string Body, string? ETag)> SendAsync(
        HttpMethod method, string url, Caller caller, HttpContent? content = null,
        IDictionary<string, string>? extraHeaders = null)
    {
        var (status, body, etag, _) = await SendCoreAsync(method, url, caller, content, extraHeaders);
        return (status, body, etag);
    }

    private async Task<(HttpStatusCode Status, string Body, string? ETag, IReadOnlyDictionary<string, string> Headers)> SendCoreAsync(
        HttpMethod method, string url, Caller caller, HttpContent? content, IDictionary<string, string>? extraHeaders)
    {
        if (!string.IsNullOrEmpty(caller.Query))
            url += (url.Contains('?') ? "&" : "?") + caller.Query;

        using var request = new HttpRequestMessage(method, url) { Content = content };
        foreach (var (key, value) in CallerHeaders(caller)) request.Headers.TryAddWithoutValidation(key, value);
        if (extraHeaders is not null)
            foreach (var (key, value) in extraHeaders) request.Headers.TryAddWithoutValidation(key, value);

        using var response = await _http.SendAsync(request);
        var etag = response.Headers.ETag?.ToString()
                   ?? (response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in response.Headers.Concat(response.Content.Headers))
            headers[key] = string.Join(", ", value);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), etag, headers);
    }

    private static Dictionary<string, string> CallerHeaders(Caller caller)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-request-id"] = Guid.NewGuid().ToString(),
            ["x-device-id"] = "integration-test-device",
            ["x-token-id"] = "integration-test-token",
        };
        if (caller.Sub is not null) headers["sub"] = caller.Sub;
        if (caller.ActSub is not null) headers["act_sub"] = caller.ActSub;
        if (caller.Role is not null) headers["role"] = caller.Role;
        if (caller.Headers is not null)
            foreach (var (key, value) in caller.Headers) headers[key] = value;
        return headers;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "vnext.config.json"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("vnext.config.json not found above " + AppContext.BaseDirectory);
    }
}
