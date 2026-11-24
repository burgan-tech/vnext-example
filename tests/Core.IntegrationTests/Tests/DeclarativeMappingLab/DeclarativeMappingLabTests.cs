using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;
using Core.IntegrationTests.Tests.ErrorBoundaryLab;

namespace Core.IntegrationTests.Tests.DeclarativeMappingLab;

/// <summary>
/// Declarative task mappings (vnext #1229): a task binding carries <c>input</c> / <c>output</c> blocks
/// (<c>select</c>, <c>into</c>, <c>set</c> with Dynamic Expresso expressions) instead of a C#
/// <c>IMapping</c>. One HTTP task (<c>dml-http-echo</c>) calls a MockLab route that echoes the request
/// body fields and the <c>x-channel</c> header back, so the response itself proves what the input
/// block wrote, and the instance data proves what the output block wrote.
/// <para>
/// The task URL is literal (<c>http://localhost:3001</c>, MockLab's default) on purpose: the other labs
/// resolve an <c>API_BASEURL</c> placeholder in a C# InputHandler, and a declarative binding has no
/// URL surface. The scenario therefore needs hosts that run as local binaries (<c>run-docker.sh up</c>).
/// </para>
/// </summary>
public class DeclarativeMappingLabTests : WorkflowTestBase
{
    private const string Workflow = "declarative-mapping-lab";

    public DeclarativeMappingLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// A transition <c>onExecutionTasks</c> binding with both blocks. Input: <c>select $.data.customer
    /// into $.body.customer</c> plus <c>set</c> on <c>$.body.customerId</c> and the <c>x-channel</c> header,
    /// on top of the task's static body (<c>source: static</c>, which must survive). Output: the echo
    /// subtree <c>into $.dml.echo</c>, then <c>set</c> entries for every variable kind — <c>result</c>
    /// arithmetic, <c>statusCode</c>, camelCase <c>instance</c> members, <c>now</c>, <c>user.available</c>
    /// (a caller-driven hop), <c>input</c> (the transition body) — and one expression over a missing
    /// intermediate (<c>data.nope.deeper</c>), which must write nothing and not fail.
    /// </summary>
    [SkippableFact]
    public async Task TransitionTask_InputAndOutputBlocks_WriteTheExpectedData()
    {
        await SkipUnlessMockLabAsync();
        var key = NewKey("dml-tr");
        var instanceId = await StartAsync(Workflow, new { key, attributes = new { customer = new { id = "c-42", name = "Ada" } } });
        await WaitUntilSettledAsync(Workflow, instanceId);

        await RunAcceptedAsync(Workflow, instanceId, "run-transition-mapping", body: new { amount = 5 });
        await AssertCompletedInAsync(instanceId, "transition-mapped");

        var dml = (await GetAttributesAsync(Workflow, instanceId)).GetProperty("dml");
        var echo = dml.GetProperty("echo");
        Assert.Equal("c-42", echo.GetProperty("customerId").GetString());     // input set on $.body
        Assert.Equal("Ada", echo.GetProperty("customerName").GetString());    // input select + into
        Assert.Equal("static", echo.GetProperty("source").GetString());       // the task's static body kept
        Assert.Equal("web", echo.GetProperty("channel").GetString());         // input set on $.headers
        // input set on $.url: API_BASEURL from host config (Example:ApiBaseUrl) + a urlEncode'd query value;
        // the output phase's `task` is the config the input phase left
        var calledUrl = dml.GetProperty("calledUrl").GetString();
        Assert.DoesNotContain("API_BASEURL", calledUrl);
        Assert.EndsWith("/api/dml/echo?customer=c-42", calledUrl);

        Assert.Equal(97, dml.GetProperty("score").GetInt32());                // result.body.score.AsInt32() + 10
        Assert.Equal(200, dml.GetProperty("status").GetInt32());              // result.statusCode
        Assert.Equal("ready", dml.GetProperty("state").GetString());          // instance.currentState before ChangeState
        Assert.Equal(key, dml.GetProperty("instanceKey").GetString());        // instance.key
        Assert.True(DateTimeOffset.TryParse(dml.GetProperty("checkedAt").GetString(), out _), "now must be a timestamp");
        Assert.True(dml.GetProperty("callerKnown").GetBoolean());             // user.available on a User hop
        Assert.Equal(5, dml.GetProperty("inputAmount").GetInt32());           // input = the transition body
        Assert.False(dml.TryGetProperty("missing", out _), "a missing intermediate must write nothing");
    }

    /// <summary>
    /// A state <c>onEntries</c> binding whose output has <c>select $.body</c> and no <c>into</c>: the
    /// selected object merges at the data root (like an OutputHandler returning <c>Data</c>), and
    /// <c>instance.currentState</c> read on entry is the entered state.
    /// </summary>
    [SkippableFact]
    public async Task EntryTask_SelectWithoutInto_MergesAtTheDataRoot()
    {
        await SkipUnlessMockLabAsync();
        var instanceId = await StartAsync(Workflow, new { key = NewKey("dml-entry"), attributes = new { customer = new { id = "c-7" } } });
        await WaitUntilSettledAsync(Workflow, instanceId);

        await RunAcceptedAsync(Workflow, instanceId, "run-entry-mapping");
        await AssertCompletedInAsync(instanceId, "entry-mapped");

        var data = await GetAttributesAsync(Workflow, instanceId);
        Assert.Equal(87, data.GetProperty("score").GetInt32());
        Assert.Equal("gold", data.GetProperty("level").GetString());
        Assert.Equal("c-7", data.GetProperty("echo").GetProperty("customerId").GetString());
        Assert.Equal("entry-mapped", data.GetProperty("entryState").GetString());
        Assert.Equal("c-7", data.GetProperty("customer").GetProperty("id").GetString()); // start data kept
    }

    /// <summary>
    /// An output expression that fails at run time (<c>"gold".AsInt32()</c>) is not a missing value:
    /// it fails the mapping, and with no error boundary the instance faults (vnext #1257) without
    /// writing any part of the delta.
    /// </summary>
    [SkippableFact]
    public async Task OutputExpressionError_FaultsTheInstance_AndWritesNothing()
    {
        await SkipUnlessMockLabAsync();
        var instanceId = await StartAsync(Workflow, new { key = NewKey("dml-bad"), attributes = new { customer = new { id = "c-9" } } });
        await WaitUntilSettledAsync(Workflow, instanceId);

        await RunAcceptedAsync(Workflow, instanceId, "run-bad-cast");
        await WaitUntilSettledAsync(Workflow, instanceId);

        var (_, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("F", status);
        var data = await GetAttributesAsync(Workflow, instanceId);
        Assert.False(data.TryGetProperty("dml", out _), "a failed mapping must not write a partial delta");
    }

    /// <summary>
    /// Publish validation runs on the definition: a binding carrying both a C# <c>mapping</c> and a
    /// declarative block is refused with a camelCase location pointing at the binding, and an output
    /// expression that does not compile is refused with its <c>set</c> key in the location.
    /// </summary>
    [Theory]
    [InlineData("both", "cannot carry both a 'mapping' script")]
    [InlineData("bad-expression", "output.set['$.dml.x']")]
    public async Task Publish_RejectsInvalidBindings(string variant, string expected)
    {
        var definition = LoadDefinition();
        definition["key"] = $"declarative-mapping-lab-{variant}";
        definition["version"] = $"1.0.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000}";
        var binding = definition["attributes"]!["states"]![0]!["transitions"]![0]!["onExecutionTasks"]![0]!.AsObject();
        if (variant == "both")
            binding["mapping"] = new JsonObject { ["location"] = "./src/None.csx", ["code"] = "cmV0dXJuIG51bGw7" };
        else
            binding["output"] = new JsonObject { ["set"] = new JsonObject { ["$.dml.x"] = "result.body.score +" } };

        var (status, body) = await SendRawJsonAsync(
            HttpMethod.Post, "api/v1/definitions/publish", definition.ToJsonString(), Headers());

        Assert.True(status == HttpStatusCode.BadRequest, $"publish answered {(int)status}: {body}");
        Assert.Contains(expected, body);
        Assert.Contains("states[ready].transitions[run-transition-mapping].onExecutionTasks[0]", body);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string NewKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task AssertCompletedInAsync(string instanceId, string expectedState)
    {
        await WaitForInstanceStateAsync(Workflow, instanceId, expectedState, timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(Workflow, instanceId);
        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal(expectedState, state);
        Assert.Equal("C", status);
    }

    private static async Task SkipUnlessMockLabAsync()
    {
        using var mocklab = new MockLabAdminClient();
        Skip.If(!await mocklab.IsUpAsync(),
            $"MockLab is not reachable at {mocklab.BaseUrl} — start it with `docker compose up -d` in the repo root.");
    }

    private static JsonObject LoadDefinition()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "vnext.config.json")))
            directory = directory.Parent;
        var path = Path.Combine(directory!.FullName, "core", "Workflows", Workflow, $"{Workflow}.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }
}
