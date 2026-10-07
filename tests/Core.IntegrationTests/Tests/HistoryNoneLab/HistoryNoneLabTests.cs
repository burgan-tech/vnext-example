using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.HistoryNoneLab;

/// <summary>
/// history-none-lab (vnext#1006): <c>attributes.history: "none"</c> marks a one-shot flow that writes no
/// transition or task history and a single, buffered data row. HTTP-only: the row counts that prove the
/// single write are the manual Postgres step in the README.
/// </summary>
public class HistoryNoneLabTests : WorkflowTestBase
{
    private const string OneShot = "hn-oneshot";

    public HistoryNoneLabTests(VNextTestEnvironment environment) : base(environment) { }

    [Theory]
    [InlineData(250, "hn-done-high")]
    [InlineData(40, "hn-done-low")]
    public async Task OneShot_CompletesOnBufferedData_AndWritesNoHistory(int amount, string finish)
    {
        var id = await StartAsync(OneShot, new { amount });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(OneShot, id)).Status == "C",
            $"hn-oneshot never completed — {await DescribeAsync(OneShot, id)}", TimeSpan.FromSeconds(60));

        Assert.Equal(finish, (await GetInstanceStateAsync(OneShot, id)).State);

        // Every task and rule read the in-memory buffer; the single flushed row carries all of it.
        var data = await GetAttributesAsync(OneShot, id);
        Assert.Equal(amount, data.GetProperty("amount").GetInt32());
        Assert.True(data.GetProperty("seeded").GetBoolean());
        Assert.Equal(1, data.GetProperty("left").GetInt32());
        Assert.Equal(2, data.GetProperty("right").GetInt32());
        Assert.Equal(3m, data.GetProperty("summary").GetDecimal());

        Assert.Empty(await HistoryAsync(OneShot, id));
        Assert.Empty(await TasksAsync(OneShot, id));
    }

    [Fact]
    public async Task FullHistoryControl_StillWritesHistory()
    {
        const string Control = "hn-full-control";
        var id = await StartAsync(Control, new { amount = 250 });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Control, id)).Status == "C",
            $"hn-full-control never completed — {await DescribeAsync(Control, id)}", TimeSpan.FromSeconds(60));

        Assert.NotEmpty(await HistoryAsync(Control, id));
        Assert.NotEmpty(await TasksAsync(Control, id));
    }

    [Fact]
    public async Task RestAtNonFinishState_FaultsWithNotTerminalIncident_AndKeepsTheData()
    {
        const string Stuck = "hn-stuck";
        var id = await StartAsync(Stuck, new { amount = 7 });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Stuck, id)).Status == "F",
            $"hn-stuck never faulted — {await DescribeAsync(Stuck, id)}", TimeSpan.FromSeconds(60));

        Assert.Equal("hn-gate", (await GetInstanceStateAsync(Stuck, id)).State);
        var (incidentStatus, incident) = await ActiveIncidentAsync(Stuck, id);
        Assert.True(incidentStatus == HttpStatusCode.OK, $"no active incident: {incident}");
        Assert.Contains("Instance:100046", incident, StringComparison.Ordinal);
        Assert.Equal(7, (await GetAttributesAsync(Stuck, id)).GetProperty("amount").GetInt32());
    }

    [Fact]
    public async Task TaskFault_WritesTheBufferedData_AndRetryIs409()
    {
        const string Fault = "hn-fault";
        var id = await StartAsync(Fault, new { amount = 9 });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Fault, id)).Status == "F",
            $"hn-fault never faulted — {await DescribeAsync(Fault, id)}", TimeSpan.FromSeconds(60));

        Assert.Equal(9, (await GetAttributesAsync(Fault, id)).GetProperty("amount").GetInt32());

        var (status, body) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{Fault}/instances/{id}/retry?sync=true", new { }, Headers());
        Assert.True(status == HttpStatusCode.Conflict, $"retry answered {(int)status}: {body}");
        Assert.Contains("Instance:100045", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("manual-transition")]
    [InlineData("scheduled-transition")]
    [InlineData("event-transition")]
    [InlineData("shared-transition")]
    [InlineData("cancel")]
    [InlineData("exit")]
    [InlineData("update-data")]
    [InlineData("timeout")]
    [InlineData("long-poll")]
    [InlineData("wizard")]
    [InlineData("human")]
    [InlineData("no-finish")]
    [InlineData("no-transitions")]
    [InlineData("cycle")]
    public async Task Publish_RejectsAShapeThatIsNotOneShot(string violation)
    {
        var definition = ProbeDefinition(violation);
        var attributes = definition["attributes"]!.AsObject();
        var collect = State(definition, "hn-collect");
        var firstAuto = collect["transitions"]![0]!.AsObject();
        JsonNode Manual(string key) => JsonNode.Parse($$"""
            { "key": "{{key}}", "target": "hn-done-low", "triggerType": 0, "versionStrategy": "Minor",
              "labels": [{ "language": "en-US", "label": "X" }] }
            """)!;

        switch (violation)
        {
            case "manual-transition": firstAuto["triggerType"] = 0; firstAuto.Remove("triggerKind"); break;
            case "scheduled-transition":
                firstAuto["triggerType"] = 2; firstAuto.Remove("triggerKind");
                firstAuto["timer"] = JsonNode.Parse("""{ "reset": "N", "duration": "PT1M" }"""); break;
            case "event-transition": firstAuto["triggerType"] = 3; firstAuto.Remove("triggerKind"); break;
            case "shared-transition":
                var shared = Manual("poke"); shared["availableIn"] = new JsonArray("hn-collect");
                attributes["sharedTransitions"] = new JsonArray(shared); break;
            case "cancel": attributes["cancel"] = Manual("cancel"); break;
            case "exit": attributes["exit"] = Manual("exit"); break;
            case "update-data": var update = Manual("update-data"); update["target"] = "$self"; attributes["updateData"] = update; break;
            case "timeout":
                attributes["timeout"] = JsonNode.Parse("""
                    { "key": "expire", "target": "hn-done-low", "versionStrategy": "Minor",
                      "timer": { "reset": "N", "duration": "PT1M" } }
                    """); break;
            case "long-poll": collect["interaction"] = JsonNode.Parse("""{ "longPoll": { "terminate": true } }"""); break;
            case "wizard": collect["stateType"] = 5; break;
            case "human": collect["subType"] = 6; break;
            case "no-finish":
                var states = attributes["states"]!.AsArray();
                foreach (var finish in states.Where(s => s!["stateType"]!.GetValue<int>() == 3).ToList()) states.Remove(finish);
                foreach (var t in State(definition, "hn-route")["transitions"]!.AsArray()) t!["target"] = "hn-collect";
                break;
            case "no-transitions": State(definition, "hn-route")["transitions"] = new JsonArray(); break;
            case "cycle": foreach (var t in State(definition, "hn-route")["transitions"]!.AsArray()) t!["target"] = "hn-collect"; break;
        }

        var (status, body) = await PublishAsync(definition);

        Assert.True(status == HttpStatusCode.BadRequest, $"publish answered {(int)status}: {body}");
        Assert.Contains("history 'none'", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_RejectsAnUnknownHistoryValue()
    {
        var definition = ProbeDefinition("unknown-value");
        definition["attributes"]!["history"] = "partial";

        var (status, body) = await PublishAsync(definition);

        Assert.True(status == HttpStatusCode.BadRequest, $"publish answered {(int)status}: {body}");
        Assert.Contains("Unknown history mode", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_AcceptsAWorkflowLevelEventStart()
    {
        var definition = ProbeDefinition("event-start");
        definition["attributes"]!["event"] = JsonNode.Parse("""
            { "action": "start", "mapping": { "location": "./src/X.csx", "code": "cmV0dXJuIG51bGw7" } }
            """);

        var (status, body) = await PublishAsync(definition);

        Assert.True((int)status < 300, $"publish answered {(int)status}: {body}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<List<JsonElement>> HistoryAsync(string workflow, string id)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{workflow}/instances/{id}/transitions", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"history answered {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("transitions").EnumerateArray().ToList();
    }

    private async Task<List<JsonElement>> TasksAsync(string workflow, string id)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{workflow}/instances/{id}/functions/tasks", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"tasks function answered {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().ToList();
    }

    private Task<(HttpStatusCode Status, string Body)> ActiveIncidentAsync(string workflow, string id) =>
        SendRawAsync(HttpMethod.Get, $"api/v1/core/workflows/{workflow}/instances/{id}/incidents/active", headers: Headers());

    private static JsonObject State(JsonObject definition, string key) =>
        definition["attributes"]!["states"]!.AsArray().First(s => s!["key"]!.GetValue<string>() == key)!.AsObject();

    /// <summary>
    /// A throw-away copy of hn-oneshot under a probe key and a per-run version, so a 409 "already
    /// exists" can never stand in for the rejection under test.
    /// </summary>
    private static JsonObject ProbeDefinition(string tag)
    {
        var path = Path.Combine(RepoRoot(), "core", "Workflows", "history-none-lab", "hn-oneshot.json");
        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        definition["key"] = $"hn-oneshot-probe-{tag}";
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

        throw new InvalidOperationException("vnext.config.json not found above the test output directory");
    }
}
