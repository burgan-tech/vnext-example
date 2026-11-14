using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AiAgentLab;

/// <summary>
/// AI Agent state (vnext#1105, <c>stateType: 6</c>), end to end on the runtime's deterministic
/// <c>local</c> provider. The agent (<c>core/Agents/ai-agent-lab/ai-triage-agent.json</c>) calls each of
/// its tools once with arguments built from the tool schema, then answers from its output schema —
/// so the model decision is always the FIRST candidate and <c>category</c> is always <c>question</c>.
/// <para>
/// Three flows, one decision mode each: <c>ai-agent-lab</c> (model decision, a script tool and a task
/// tool, flow memory, <c>maxEntries</c> + <c>onExhausted</c>), <c>ai-agent-lab-script</c>
/// (<c>IAgentDecisionMapping</c>, and the fallback when it names no candidate) and
/// <c>ai-agent-lab-rules</c> (the transitions' rules read the agent's output).
/// </para>
/// <para>
/// The SDK publishes only the seven classic component types, and the <c>sys-agents</c> flow is not in
/// the system package yet, so this class publishes both once per run: the <c>sys-agents</c> workflow
/// (verbatim copy of <c>sys-mappings</c> under <c>fixtures/</c>) and then the agent. Publish is
/// version-immutable; "already published" is fine on a re-run.
/// </para>
/// </summary>
public class AiAgentLabTests : WorkflowTestBase
{
    private const string ModelFlow = "ai-agent-lab";
    private const string ScriptFlow = "ai-agent-lab-script";
    private const string RulesFlow = "ai-agent-lab-rules";
    private const string ApprovalFlow = "ai-agent-lab-approval";

    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private static bool _published;

    public AiAgentLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// Model decision: the agent runs on entry, calls both tools, writes its output and <c>$agent</c>
    /// to <c>triage</c>, and its decision (the first candidate, <c>is-question</c>) is the automatic
    /// transition taken. The run is listed by <c>functions/agent-runs</c>, with its tool calls by run id.
    /// </summary>
    [Fact]
    public async Task ModelDecision_TakesTheChosenCandidate_AndRecordsTheRun()
    {
        await EnsurePublishedAsync();
        var instanceId = await StartAsync(ModelFlow, new { message = "Where is my card?" });

        await WaitForInstanceStateAsync(ModelFlow, instanceId, "answered", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ModelFlow, instanceId);
        var (_, status) = await GetInstanceStateAsync(ModelFlow, instanceId);
        Assert.Equal("A", status);

        var triage = (await GetAttributesAsync(ModelFlow, instanceId)).GetProperty("triage");
        Assert.Equal("question", triage.GetProperty("category").GetString());
        var agent = triage.GetProperty("$agent");
        Assert.Equal("Completed", agent.GetProperty("status").GetString());
        Assert.Equal("is-question", agent.GetProperty("decision").GetString());
        Assert.Equal("Model", agent.GetProperty("decisionSource").GetString());
        Assert.Equal("instance", agent.GetProperty("session").GetProperty("scope").GetString());
        Assert.False(agent.GetProperty("session").GetProperty("resumed").GetBoolean());

        var toolCalls = agent.GetProperty("toolCalls").EnumerateArray().ToList();
        Assert.Equal(new[] { "bump", "lab_script" }, toolCalls.Select(c => c.GetProperty("name").GetString()).OrderBy(n => n));
        Assert.All(toolCalls, c => Assert.True(c.GetProperty("ok").GetBoolean(), $"tool call failed: {c}"));

        var runs = await GetRunsAsync(ModelFlow, instanceId);
        var run = Assert.Single(runs);
        Assert.Equal("classifying", run.GetProperty("state").GetString());
        Assert.Equal(1, run.GetProperty("entryNo").GetInt32());
        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal("is-question", run.GetProperty("selectedTransition").GetString());
        Assert.Equal("model", run.GetProperty("decisionSource").GetString());
        Assert.Equal(2, run.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(agent.GetProperty("runId").GetString(), run.GetProperty("id").GetString());

        // One run by id: its tool calls, metadata only (no arguments or results are served).
        var (detailStatus, detailRaw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{ModelFlow}/instances/{instanceId}/functions/agent-runs?runId={run.GetProperty("id").GetString()}",
            headers: Headers());
        Assert.True(detailStatus == HttpStatusCode.OK, $"agent-runs?runId answered {(int)detailStatus}: {detailRaw}");
        Assert.Contains("lab_script", detailRaw);
        Assert.DoesNotContain("from-agent", detailRaw);
    }

    /// <summary>
    /// Re-entry: the second entry resumes the flow-scoped session; the third exceeds
    /// <c>policy.maxEntries: 2</c> and leaves through <c>onExhausted</c> without calling the provider.
    /// The exhausted run's <c>$agent</c> must not carry the tool calls or session of the run before it
    /// (instance data merges per key and a null never clears a key — a runtime defect found here).
    /// </summary>
    [Fact]
    public async Task ReEntry_ResumesTheSession_ThenLeavesThroughOnExhausted()
    {
        await EnsurePublishedAsync();
        var instanceId = await StartAsync(ModelFlow, new { message = "first" });
        await WaitForInstanceStateAsync(ModelFlow, instanceId, "answered", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ModelFlow, instanceId);

        await RunAcceptedAsync(ModelFlow, instanceId, "ask-again", new { message = "second" });
        await WaitUntilAsync(async () => (await GetRunsAsync(ModelFlow, instanceId)).Count == 2, "second agent run recorded");
        await WaitForInstanceStateAsync(ModelFlow, instanceId, "answered", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ModelFlow, instanceId);

        var second = (await GetAttributesAsync(ModelFlow, instanceId)).GetProperty("triage").GetProperty("$agent");
        Assert.Equal("is-question", second.GetProperty("decision").GetString());
        Assert.True(second.GetProperty("session").GetProperty("resumed").GetBoolean(), $"second entry did not resume: {second}");

        await RunAcceptedAsync(ModelFlow, instanceId, "ask-again", new { message = "third" });
        await WaitForInstanceStateAsync(ModelFlow, instanceId, "exhausted", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ModelFlow, instanceId);
        var (_, status) = await GetInstanceStateAsync(ModelFlow, instanceId);
        Assert.Equal("C", status);

        var third = (await GetAttributesAsync(ModelFlow, instanceId)).GetProperty("triage").GetProperty("$agent");
        Assert.Equal("BudgetExceeded", third.GetProperty("status").GetString());
        Assert.Equal("too-many", third.GetProperty("decision").GetString());
        Assert.Equal("Exhausted", third.GetProperty("decisionSource").GetString());
        Assert.Empty(third.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal("none", third.GetProperty("session").GetProperty("scope").GetString());
        Assert.Equal(0, third.GetProperty("iterations").GetInt32());

        var runs = await GetRunsAsync(ModelFlow, instanceId);
        Assert.Equal(new[] { 3, 2, 1 }, runs.Select(r => r.GetProperty("entryNo").GetInt32()));
        Assert.True(runs[1].GetProperty("sessionResumed").GetBoolean());
        Assert.Equal("exhausted", runs[0].GetProperty("decisionSource").GetString());
        Assert.Equal(0, runs[0].GetProperty("toolCallCount").GetInt32());
    }

    /// <summary>Script decision: the mapping returns a candidate key and that transition is taken.</summary>
    [Fact]
    public async Task ScriptDecision_TakesTheCandidateTheScriptReturns()
    {
        await EnsurePublishedAsync();
        var instanceId = await StartAsync(ScriptFlow, new { message = "my card was stolen", pick = "is-fraud" });

        await WaitForInstanceStateAsync(ScriptFlow, instanceId, "escalated", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ScriptFlow, instanceId);

        var agent = (await GetAttributesAsync(ScriptFlow, instanceId)).GetProperty("triage").GetProperty("$agent");
        Assert.Equal("is-fraud", agent.GetProperty("decision").GetString());
        Assert.Equal("Script", agent.GetProperty("decisionSource").GetString());
        // tools.exclude removes lab_script on this state; bump still runs.
        Assert.Equal("bump", Assert.Single(agent.GetProperty("toolCalls").EnumerateArray()).GetProperty("name").GetString());
    }

    /// <summary>
    /// Script decision naming no candidate: there is no silent default, the state leaves through its
    /// <c>fallback</c>, and the run says why.
    /// </summary>
    [Fact]
    public async Task ScriptDecision_NotACandidate_TakesTheFallback()
    {
        await EnsurePublishedAsync();
        var instanceId = await StartAsync(ScriptFlow, new { message = "?", pick = "no-such-transition" });

        await WaitForInstanceStateAsync(ScriptFlow, instanceId, "failed", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ScriptFlow, instanceId);

        var agent = (await GetAttributesAsync(ScriptFlow, instanceId)).GetProperty("triage").GetProperty("$agent");
        Assert.Equal("classify-failed", agent.GetProperty("decision").GetString());
        Assert.Equal("Fallback", agent.GetProperty("decisionSource").GetString());
        Assert.Contains("no-such-transition", agent.GetProperty("reason").GetString());

        var run = Assert.Single(await GetRunsAsync(ScriptFlow, instanceId));
        Assert.Equal("classify-failed", run.GetProperty("selectedTransition").GetString());
        Assert.Equal("fallback", run.GetProperty("decisionSource").GetString());
    }

    /// <summary>
    /// Rules mode: the agent only produces data; the transitions' own rules pick the winner from it
    /// (<c>to-question</c> reads <c>triage.category</c>). The rule-less fallback is evaluated last, so it
    /// does not win while a rule is satisfied. The winner is written back to the run's decision row.
    /// </summary>
    [Fact]
    public async Task RulesMode_TheTransitionsRulesDecideFromTheAgentsOutput()
    {
        await EnsurePublishedAsync();
        var instanceId = await StartAsync(RulesFlow, new { message = "Where is my card?" });

        await WaitForInstanceStateAsync(RulesFlow, instanceId, "answered", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(RulesFlow, instanceId);

        var triage = (await GetAttributesAsync(RulesFlow, instanceId)).GetProperty("triage");
        Assert.Equal("question", triage.GetProperty("category").GetString());
        Assert.Equal("Rules", triage.GetProperty("$agent").GetProperty("decisionSource").GetString());
        Assert.Empty(triage.GetProperty("$agent").GetProperty("toolCalls").EnumerateArray());

        var run = Assert.Single(await GetRunsAsync(RulesFlow, instanceId));
        Assert.Equal("to-question", run.GetProperty("selectedTransition").GetString());
        Assert.Equal("rules", run.GetProperty("decisionSource").GetString());
    }

    /// <summary>
    /// Tool approval: the model calls <c>pay</c> (requiresApproval) and <c>lookup</c> in one turn; the run
    /// stops before either runs, journals <c>pay</c> as ApprovalPending and leaves through
    /// <c>onApprovalRequest</c> to <c>review</c> — a plain intermediate state. Answering approved resumes the
    /// conversation from its session: both calls run and the model decides. The run kept a full transcript
    /// holding the seed messages and the labelled data context.
    /// </summary>
    [Fact]
    public async Task Approval_HoldsTheCall_ThenResumesOnApproval()
    {
        await EnsurePublishedAsync();
        var instanceId = await StartAsync(ApprovalFlow, new { message = "pay my bill", customer = new { segment = "retail" } });

        await WaitForInstanceStateAsync(ApprovalFlow, instanceId, "review", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ApprovalFlow, instanceId);

        var held = (await GetAttributesAsync(ApprovalFlow, instanceId)).GetProperty("assessment").GetProperty("$agent");
        Assert.Equal("ApprovalRequested", held.GetProperty("status").GetString());
        Assert.Equal("needs-approval", held.GetProperty("decision").GetString());
        Assert.Equal("ApprovalRequest", held.GetProperty("decisionSource").GetString());
        var pending = held.GetProperty("pendingApprovals").EnumerateArray().ToList();
        Assert.Contains(pending, p => p.GetProperty("tool").GetString() == "pay" && p.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Empty(held.GetProperty("toolCalls").EnumerateArray().Where(c => c.GetProperty("ok").GetBoolean()));

        var heldRun = Assert.Single(await GetRunsAsync(ApprovalFlow, instanceId));
        Assert.Equal("approvalRequested", heldRun.GetProperty("status").GetString());
        var (detailStatus, detail) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{ApprovalFlow}/instances/{instanceId}/functions/agent-runs?runId={heldRun.GetProperty("id").GetString()}&include=transcript",
            headers: Headers());
        Assert.True(detailStatus == HttpStatusCode.OK, $"agent-runs detail answered {(int)detailStatus}: {detail}");
        Assert.Contains("approvalPending", detail);
        Assert.Contains("Example request: what is my limit?", detail);
        Assert.Contains("retail", detail); // prompt.context: data.customer rendered into a labelled <data> block

        await RunAcceptedAsync(ApprovalFlow, instanceId, "decide", new { approval = new { approved = true } });
        await WaitForInstanceStateAsync(ApprovalFlow, instanceId, "answered", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ApprovalFlow, instanceId);

        var resumed = (await GetAttributesAsync(ApprovalFlow, instanceId)).GetProperty("assessment").GetProperty("$agent");
        Assert.Equal("Completed", resumed.GetProperty("status").GetString());
        Assert.Equal("is-question", resumed.GetProperty("decision").GetString());
        Assert.Empty(resumed.GetProperty("pendingApprovals").EnumerateArray());
        var ran = resumed.GetProperty("toolCalls").EnumerateArray().Where(c => c.GetProperty("ok").GetBoolean()).Select(c => c.GetProperty("name").GetString()).OrderBy(n => n);
        Assert.Equal(new[] { "lookup", "pay" }, ran);
    }

    /// <summary>A denial resumes the conversation too: <c>pay</c> never runs and is journaled Denied.</summary>
    [Fact]
    public async Task Approval_Denied_ResumesWithoutRunningTheCall()
    {
        await EnsurePublishedAsync();
        var instanceId = await StartAsync(ApprovalFlow, new { message = "pay my bill", customer = new { segment = "retail" } });
        await WaitForInstanceStateAsync(ApprovalFlow, instanceId, "review", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ApprovalFlow, instanceId);

        await RunAcceptedAsync(ApprovalFlow, instanceId, "decide", new { approval = new { approved = false, reason = "over the limit" } });
        await WaitForInstanceStateAsync(ApprovalFlow, instanceId, "answered", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(ApprovalFlow, instanceId);

        var agent = (await GetAttributesAsync(ApprovalFlow, instanceId)).GetProperty("assessment").GetProperty("$agent");
        Assert.Equal("Completed", agent.GetProperty("status").GetString());
        Assert.DoesNotContain(agent.GetProperty("toolCalls").EnumerateArray(), c => c.GetProperty("name").GetString() == "pay" && c.GetProperty("ok").GetBoolean());

        var runs = await GetRunsAsync(ApprovalFlow, instanceId);
        var (_, detail) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{ApprovalFlow}/instances/{instanceId}/functions/agent-runs?runId={runs[0].GetProperty("id").GetString()}",
            headers: Headers());
        Assert.Contains("denied", detail);
        Assert.Contains("over the limit", detail);
    }

    /// <summary>
    /// Teams. Sequential and concurrent: both participants run before the lead (one run row each, named),
    /// the lead decides. Handoff: the lead runs as <c>$router</c> and names the first participant
    /// (<c>cards</c>), which produces the output and the decision.
    /// </summary>
    [Theory]
    [InlineData("ai-agent-lab-sequential")]
    [InlineData("ai-agent-lab-concurrent")]
    [InlineData("ai-agent-lab-handoff")]
    public async Task Team_RunsParticipantsAndDecides(string workflow)
    {
        await EnsurePublishedAsync();
        var instanceId = await StartAsync(workflow, new { message = "my card is blocked" });

        await WaitForInstanceStateAsync(workflow, instanceId, "answered", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(workflow, instanceId);

        var triage = (await GetAttributesAsync(workflow, instanceId)).GetProperty("triage");
        var agent = triage.GetProperty("$agent");
        Assert.Equal("is-question", agent.GetProperty("decision").GetString());
        var participants = agent.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        var runs = await GetRunsAsync(workflow, instanceId);
        var runParticipants = runs.Select(r => r.TryGetProperty("participant", out var p) ? p.GetString() : null).ToList();

        if (workflow.EndsWith("handoff", StringComparison.Ordinal))
        {
            Assert.Equal("cards", agent.GetProperty("handoffTo").GetString());
            Assert.Equal(new[] { "$router" }, participants);
            Assert.Equal("cards handled it", triage.GetProperty("note").GetString());
            Assert.Contains(runs, r => r.GetProperty("agentKey").GetString() == "ai-cards-agent" && runParticipants[runs.IndexOf(r)] is null);
        }
        else
        {
            Assert.Equal(new[] { "cards", "loans" }, participants.OrderBy(n => n));
            Assert.Equal("question", triage.GetProperty("category").GetString());
            Assert.Equal(3, runs.Count);
            Assert.Single(runParticipants, p => p is null);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<List<JsonElement>> GetRunsAsync(string workflow, string instanceId)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{workflow}/instances/{instanceId}/functions/agent-runs", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"agent-runs answered {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>Publishes the <c>sys-agents</c> workflow and then the lab agent, once per test run.</summary>
    private async Task EnsurePublishedAsync()
    {
        await PublishGate.WaitAsync();
        try
        {
            if (_published) return;

            var root = RepoRoot();
            await PublishAsync(Path.Combine(root, "tests", "Core.IntegrationTests", "Tests", "AiAgentLab", "fixtures", "sys-agents.json"));
            foreach (var agentFile in Directory.GetFiles(Path.Combine(root, "core", "Agents", "ai-agent-lab"), "*.json").OrderBy(f => f, StringComparer.Ordinal))
                await PublishAsync(agentFile);
            _published = true;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    private async Task PublishAsync(string path)
    {
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.ToJsonString();
        var (status, body) = await SendRawJsonAsync(HttpMethod.Post, "api/v1/definitions/publish", json, Headers());
        var alreadyThere = status == HttpStatusCode.Conflict
                           || body.Contains("already", StringComparison.OrdinalIgnoreCase);
        Assert.True((int)status < 300 || alreadyThere, $"publishing {Path.GetFileName(path)} answered {(int)status}: {body}");
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
