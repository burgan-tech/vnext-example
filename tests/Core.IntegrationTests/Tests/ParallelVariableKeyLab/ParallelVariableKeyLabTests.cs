using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ParallelVariableKeyLab;

/// <summary>
/// parallel-variable-key-lab: the same SubProcess task runs twice at one order. Entries at one
/// order run in parallel and are merged by response slot; before variableKey both runs filed
/// under ToVariableName(task.key) and the merge threw "Parallel tasks produced conflicting output".
/// </summary>
public class ParallelVariableKeyLabTests : WorkflowTestBase
{
    private const string Parent = "pvk-parent";
    private const string Child = "pvk-child";

    public ParallelVariableKeyLabTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task SameTaskTwiceAtOneOrder_WithVariableKeys_EachRunKeepsItsOwnSlot()
    {
        var parentId = await StartAsync(Parent, new { });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Parent, parentId)).State == "spawned",
            $"pvk-parent never reached 'spawned' — {await DescribeAsync(Parent, parentId)}",
            TimeSpan.FromSeconds(60));

        var (_, status) = await GetInstanceStateAsync(Parent, parentId);
        Assert.NotEqual("F", status);

        var data = await GetAttributesAsync(Parent, parentId);
        var primary = data.GetProperty("primaryChildId").GetString();
        var secondary = data.GetProperty("secondaryChildId").GetString();
        var legacy = data.GetProperty("legacyChildId").GetString();

        Assert.False(string.IsNullOrEmpty(primary), "primaryChild slot was empty");
        Assert.False(string.IsNullOrEmpty(secondary), "secondaryChild slot was empty");
        Assert.False(string.IsNullOrEmpty(legacy), "legacy pvkSpawnChild slot was empty");
        Assert.Equal(3, new[] { primary, secondary, legacy }.Distinct().Count());

        foreach (var childId in new[] { primary!, secondary!, legacy! })
        {
            var (childState, _) = await GetInstanceStateAsync(Child, childId);
            Assert.Equal("waiting", childState);
        }
    }

    [Fact]
    public async Task Publish_SameTaskTwiceAtOneOrder_WithoutVariableKey_Returns400()
    {
        var definition = ProbeDefinition("no-variable-key");
        foreach (var entry in OnEntries(definition).Where(e => e!["order"]!.GetValue<int>() == 1))
            entry!.AsObject().Remove("variableKey");

        var (status, body) = await PublishAsync(definition);

        Assert.True(status == HttpStatusCode.BadRequest, $"publish answered {(int)status}: {body}");
        Assert.Contains("pvkSpawnChild", body, StringComparison.Ordinal);
        Assert.Contains("variableKey", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_SameVariableKeyTwiceAtOneOrder_Returns400()
    {
        var definition = ProbeDefinition("dup-variable-key");
        foreach (var entry in OnEntries(definition).Where(e => e!["order"]!.GetValue<int>() == 1))
            entry!["variableKey"] = "child";

        var (status, body) = await PublishAsync(definition);

        Assert.True(status == HttpStatusCode.BadRequest, $"publish answered {(int)status}: {body}");
        Assert.Contains("'child'", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_InvalidVariableKeyFormat_Returns400()
    {
        var definition = ProbeDefinition("bad-variable-key");
        OnEntries(definition)[0]!["variableKey"] = "primary-child";

        var (status, body) = await PublishAsync(definition);

        Assert.True(status == HttpStatusCode.BadRequest, $"publish answered {(int)status}: {body}");
        Assert.Contains("primary-child", body, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static JsonArray OnEntries(JsonObject definition) =>
        definition["attributes"]!["states"]!.AsArray()
            .First(s => s!["key"]!.GetValue<string>() == "spawning")!["onEntries"]!.AsArray();

    /// <summary>
    /// A throw-away copy of the on-disk parent under a probe key and a per-run version, so a 409
    /// "already exists" can never stand in for the rejection under test. Invalid definitions live
    /// here rather than under <c>core/</c>, where the SDK publisher would post them on every start.
    /// </summary>
    private static JsonObject ProbeDefinition(string tag)
    {
        var path = Path.Combine(RepoRoot(), "core", "Workflows", "parallel-variable-key-lab", "pvk-parent.json");
        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        definition["key"] = $"pvk-parent-probe-{tag}";
        definition["version"] = $"9.{DateTime.UtcNow:yyMMdd}.{(int)DateTime.UtcNow.TimeOfDay.TotalMilliseconds}";
        return definition;
    }

    private Task<(HttpStatusCode Status, string Body)> PublishAsync(JsonObject definition) =>
        SendRawJsonAsync(HttpMethod.Post, "api/v1/definitions/publish", definition.ToJsonString(), Headers());

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
