using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ImplicitStartLab;

/// <summary>
/// implicit-start-lab: a workflow may omit the Initial state (<c>stateType: 1</c>).
/// <para>
/// When no Initial state is declared, the runtime supplies an implicit source state keyed
/// <c>$start</c> (<c>WellKnownStateKeys.Start</c>); the instance is born there and
/// <c>startTransition.target</c> alone decides where it enters. <c>$start</c> is never a resting
/// point: the start transition leaves it inside the same pipeline, so a client must never be
/// stranded on it, and the only trace it leaves is the first history row's <c>fromState</c>.
/// </para>
/// <para>
/// The parent flow DECLARES an Initial state and is the regression side: an Initial-declaring
/// workflow behaves exactly as before while it starts an Initial-less child as a SubFlow. Publish
/// still refuses two Initial states and a start transition without a target.
/// </para>
/// </summary>
public class ImplicitStartLabTests : WorkflowTestBase
{
    private const string Lab = "implicit-start-lab";
    private const string Parent = "implicit-start-lab-parent";
    private const string Child = "implicit-start-lab-child";

    private const string ImplicitStart = "$start";

    public ImplicitStartLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// A synchronous start of an Initial-less flow lands on the start target (a Wizard state),
    /// Active, and the history records the hop from the implicit <c>$start</c>.
    /// </summary>
    [Fact]
    public async Task Start_WithoutInitialState_Sync_LandsOnWizard()
    {
        var instanceId = await StartAsync(Lab, new { testId = TestId("sync") });

        var (state, status) = await GetInstanceStateAsync(Lab, instanceId);
        Assert.Equal("step-1", state);
        Assert.Equal("A", status);

        var first = await FirstHistoryRowAsync(Lab, instanceId);
        Assert.Equal(ImplicitStart, first.GetProperty("fromState").GetString());
        Assert.Equal("step-1", first.GetProperty("toState").GetString());
        Assert.Equal("start-implicit", first.GetProperty("transitionId").GetString());
    }

    /// <summary>
    /// An asynchronous start answers immediately; a client polling the state function from that
    /// moment never gets an error (no 404 for an unknown state, no 500) and comes to rest on the
    /// start target. Should a poll ever catch the instance in <c>$start</c>, it must offer no
    /// transitions — the implicit state has none.
    /// </summary>
    [Fact]
    public async Task Start_WithoutInitialState_Async_PollNeverFails()
    {
        var instanceId = await StartAsyncModeAsync(Lab, new { testId = TestId("async") });

        string? etag = null;
        string? lastState = null;
        string? lastStatus = null;
        var polls = 0;

        await WaitUntilAsync(
            async () =>
            {
                polls++;
                var (code, newEtag, body) = await PollStateAsync(Lab, instanceId, etag);

                Assert.True(code == HttpStatusCode.OK || code == HttpStatusCode.NotModified,
                    $"state poll #{polls} answered {(int)code}: {body}");

                if (code == HttpStatusCode.NotModified) return lastStatus is not null && lastStatus != "B" && lastState == "step-1";

                etag = newEtag ?? etag;
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                lastState = root.GetProperty("state").GetString();
                lastStatus = root.GetProperty("status").GetString();

                if (lastState == ImplicitStart)
                {
                    var transitions = root.TryGetProperty("transitions", out var t) && t.ValueKind == JsonValueKind.Array
                        ? t.GetArrayLength()
                        : 0;
                    Assert.True(transitions == 0,
                        $"the state function offered {transitions} transition(s) while in '{ImplicitStart}': {body}");
                }

                return lastStatus != "B" && lastStatus != "C" && lastState == "step-1";
            },
            $"{Lab}/{instanceId} never came to rest on 'step-1' (last {lastState}/{lastStatus})",
            TimeSpan.FromSeconds(30));

        Assert.Equal("step-1", lastState);
        Assert.Equal("A", lastStatus);
        Assert.True(polls >= 1);
    }

    /// <summary>The Initial-less flow runs to completion through its ordinary transitions.</summary>
    [Fact]
    public async Task Start_WithoutInitialState_ThenTransition_Completes()
    {
        var instanceId = await StartAsync(Lab, new { testId = TestId("complete") });
        await WaitForInstanceStateAsync(Lab, instanceId, "step-1");

        await RunAcceptedAsync(Lab, instanceId, "next");

        var (state, status) = await GetInstanceStateAsync(Lab, instanceId);
        Assert.Equal("done", state);
        Assert.Equal("C", status);
    }

    /// <summary>
    /// A parent that declares an Initial state starts an Initial-less child through a SubFlow
    /// state: the child is born in <c>$start</c> and enters its start target, and completing the
    /// child completes the parent.
    /// </summary>
    [Fact]
    public async Task SubFlowChild_WithoutInitialState_StartsAndParentCompletes()
    {
        var parentId = await StartAsync(Parent, new { testId = TestId("subflow") });

        // Regression side: the parent's own first hop still leaves its declared Initial state.
        var parentFirst = await FirstHistoryRowAsync(Parent, parentId);
        Assert.Equal("waiting", parentFirst.GetProperty("toState").GetString());
        Assert.NotEqual(ImplicitStart, parentFirst.GetProperty("fromState").GetString());

        await WaitForInstanceStateAsync(Parent, parentId, "in-child");

        string? childId = null;
        await WaitUntilAsync(
            async () => (await GetActiveSubflowsAsync(Parent, parentId)).TryGetValue(Child, out childId),
            $"the parent never opened a '{Child}' correlation — {await DescribeAsync(Parent, parentId)}");

        await WaitForInstanceStateAsync(Child, childId!, "step-1");
        await AssertNotFaultedAsync(Child, childId!);

        var childFirst = await FirstHistoryRowAsync(Child, childId!);
        Assert.Equal(ImplicitStart, childFirst.GetProperty("fromState").GetString());
        Assert.Equal("step-1", childFirst.GetProperty("toState").GetString());

        await RunAcceptedAsync(Child, childId!, "next");

        await WaitUntilAsync(
            async () => (await GetInstanceStateAsync(Parent, parentId)).Status == "C",
            $"the parent never completed after its child did — {await DescribeAsync(Parent, parentId)}",
            TimeSpan.FromSeconds(60));

        Assert.Equal("done", (await GetInstanceStateAsync(Parent, parentId)).State);
        Assert.Equal(("done", "C"), await GetInstanceStateAsync(Child, childId!));
    }

    /// <summary>Optional is not "any number": two Initial states are still refused at publish.</summary>
    [Fact]
    public async Task Publish_WithTwoInitialStates_Returns400()
    {
        var definition = ProbeDefinition("two-initial");
        var states = definition["attributes"]!["states"]!.AsArray();
        states[0]!["stateType"] = 1;
        states.Insert(0, JsonNode.Parse("""
            {
              "key": "also-initial", "stateType": 1, "subType": 0, "versionStrategy": "Major",
              "labels": [{ "language": "en-US", "label": "Also initial" }],
              "view": null, "subFlow": null, "onEntries": [], "onExits": [],
              "transitions": [{ "key": "go", "target": "done", "triggerType": 0, "versionStrategy": "Minor",
                                "labels": [{ "language": "en-US", "label": "Go" }] }]
            }
            """));

        var (status, body) = await PublishAsync(definition);

        Assert.True(status == HttpStatusCode.BadRequest, $"publish answered {(int)status}: {body}");
        Assert.Contains("at most one initial state", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// With no Initial state the start target is the only thing that says where an instance
    /// enters, so a start transition without one — absent or empty — is a client error at
    /// publish, never a 2xx and never an unhandled 5xx.
    /// </summary>
    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    public async Task Publish_WithMissingStartTarget_IsRejected(string shape)
    {
        var definition = ProbeDefinition($"target-{shape}");
        var start = definition["attributes"]!["startTransition"]!.AsObject();
        if (shape == "absent") start.Remove("target");
        else start["target"] = "";

        var (status, body) = await PublishAsync(definition);

        Assert.True((int)status is >= 400 and < 500,
            $"a start transition with an {shape} target was answered {(int)status}: {body}");
        Assert.Contains("Target", body, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string TestId(string tag) => $"implicit-{tag}-{Guid.NewGuid():N}"[..32];

    /// <summary>Starts with <c>?sync=false</c>; the SDK client hard-codes <c>?sync=true</c>.</summary>
    private async Task<string> StartAsyncModeAsync(string workflow, object body)
    {
        var (status, raw) = await SendRawAsync(
            HttpMethod.Post, $"api/v1/core/workflows/{workflow}/instances/start?sync=false", body, Headers());

        Assert.True((int)status < 400, $"start was refused with {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("start response carried no instance id");
    }

    /// <summary>The oldest row of the instance's transition history.</summary>
    private async Task<JsonElement> FirstHistoryRowAsync(string workflow, string instanceId)
    {
        var (status, raw) = await SendRawAsync(
            HttpMethod.Get, $"api/v1/core/workflows/{workflow}/instances/{instanceId}/transitions", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"history answered {(int)status}: {raw}");

        var rows = JsonDocument.Parse(raw).RootElement.GetProperty("transitions").EnumerateArray()
            .OrderBy(row => row.GetProperty("startedAt").GetDateTimeOffset())
            .ToList();
        Assert.NotEmpty(rows);
        return rows[0];
    }

    /// <summary>
    /// A throw-away copy of the on-disk lab flow under a probe key and a per-run version, so a
    /// 409 "already exists" can never stand in for the rejection under test. Invalid definitions
    /// live here rather than under <c>core/</c>, where the SDK publisher would post them on every
    /// fixture start.
    /// </summary>
    private static JsonObject ProbeDefinition(string tag)
    {
        var path = Path.Combine(RepoRoot(), "core", "Workflows", "implicit-start-lab", "implicit-start-lab.json");
        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        definition["key"] = $"implicit-start-lab-probe-{tag}";
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
