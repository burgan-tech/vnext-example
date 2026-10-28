using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Rows N-1 and N-3 (body half): a flow WITHOUT <c>rowSecurity</c> reads exactly as it did before the feature.
/// <para>
/// For every caller variant of A-1…A-8 and P-1, the <c>rs-plain</c> (N-1) and <c>rs-plain-xr</c> (N-3, master schema
/// with <c>x-roles</c>) list and the GET of all seven population rows are normalised and byte-compared against
/// <c>golden/{flow}-{variant}.json</c>. The golden files were captured on the BASE runtime (the master commit the
/// row-security branch starts from) by running this class with <c>VNEXT_RS_GOLDEN_CAPTURE=1</c>, which writes the
/// files instead of comparing.
/// </para>
/// <para>
/// Normalisation: population ids → <c>r1</c>…<c>r7</c>, the run's <c>testId</c> → <c>&lt;testId&gt;</c>, ISO
/// timestamps → <c>&lt;ts&gt;</c>, <c>duration</c> (seconds of a completed instance) → <c>&lt;duration&gt;</c>, <c>eTag</c>/<c>entityEtag</c> values → <c>&lt;etag&gt;</c>, any other GUID →
/// <c>&lt;guid&gt;</c>. Member order is kept (a reordered body is a difference). N-3's span half (no extra
/// <c>Auth.ResolveRoles</c>, same DB span count) is measured from traces in the A/B evidence (Task 12), not here.
/// </para>
/// </summary>
public class UnchangedFlowTests : RowSecurityLabTestBase
{
    private const string CaptureVariable = "VNEXT_RS_GOLDEN_CAPTURE";

    public UnchangedFlowTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>The caller of each A-1…A-8 / P-1 row, by row name.</summary>
    private static readonly IReadOnlyDictionary<string, Caller> Variants = new Dictionary<string, Caller>
    {
        ["A1"] = new(),
        ["A2"] = new(Sub: "alice", ActSub: "alice"),
        ["A3"] = new(Sub: "alice", ActSub: "alice", Role: "menu.view"),
        ["A4"] = new(Sub: "alice", ActSub: "alice", Role: "branch-staff", Headers: Branch("BR-001")),
        ["A5"] = new(Sub: "alice", ActSub: "alice", Role: "branch-staff"),
        ["A6"] = new(Sub: "alice", ActSub: "alice", Role: "branch-staff,menu.view,report.read", Headers: Branch("BR-001")),
        ["A7"] = new(Sub: "alice", ActSub: "alice", Role: "branch-staff,hq-auditor", Headers: Branch("BR-001")),
        ["A8"] = new(Sub: "alice", ActSub: "alice", Role: HqAuditor),
        ["P1"] = new(Sub: "alice", ActSub: "alice", Role: "rs-p-header-eq"),
    };

    public static IEnumerable<object[]> Cases() =>
        from flow in new[] { "rs-plain", "rs-plain-xr" }
        from variant in Variants.Keys
        select new object[] { flow, variant };

    /// <summary>N-1 (<c>rs-plain</c>) / N-3 body (<c>rs-plain-xr</c>): list + GET bodies equal the base-runtime golden.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task N1_N3_NonOptedFlow_ReadsAsOnTheBaseRuntime(string flow, string variant)
    {
        var p = await SeedAsync(flow);
        var caller = Variants[variant];

        var snapshot = new JsonObject();
        var (listStatus, listBody) = await ListRawAsync(flow, caller);
        snapshot["list"] = Entry(listStatus, listBody, p);

        var gets = new JsonObject();
        string[] names = ["r1", "r2", "r3", "r4", "r5", "r6", "r7"];
        for (var i = 0; i < names.Length; i++)
        {
            var (status, body, _) = await GetAsync(flow, p.All[i].ToString(), caller);
            gets[names[i]] = Entry(status, body, p);
        }

        snapshot["get"] = gets;
        var actual = snapshot.ToJsonString(Options) + "\n";

        var path = Path.Combine(RepoRoot(), "tests", "Core.IntegrationTests", "Tests", "RowSecurityLab", "golden",
            $"{flow}-{variant}.json");
        if (System.Environment.GetEnvironmentVariable(CaptureVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"golden {path} missing — capture it on the base runtime ({CaptureVariable}=1)");
        var golden = await File.ReadAllTextAsync(path);
        Assert.Equal(golden, actual);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex Timestamp = new(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?",
        RegexOptions.Compiled);

    private static readonly Regex Duration = new(@"(""duration""\s*:\s*)-?[0-9][0-9.eE+-]*", RegexOptions.Compiled);

    private static readonly Regex AnyGuid = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private static readonly Regex Etag = new(@"(""(?:eTag|entityEtag|etag)""\s*:\s*"")(?:\\""|[^""])*("")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>One status + normalised body; a non-JSON body is kept as a normalised string.</summary>
    private static JsonObject Entry(HttpStatusCode status, string body, Population p)
    {
        var text = body;
        string[] names = ["r1", "r2", "r3", "r4", "r5", "r6", "r7"];
        for (var i = 0; i < names.Length; i++)
            text = text.Replace(p.All[i].ToString(), names[i], StringComparison.OrdinalIgnoreCase);
        text = text.Replace(p.TestId, "<testId>", StringComparison.Ordinal);
        text = Etag.Replace(text, "$1<etag>$2");
        text = Timestamp.Replace(text, "<ts>");
        text = Duration.Replace(text, "$1\"<duration>\"");
        text = AnyGuid.Replace(text, "<guid>");

        JsonNode? node;
        try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
        catch (JsonException) { node = JsonValue.Create(text); }

        return new JsonObject { ["status"] = (int)status, ["body"] = node };
    }
}
