using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;
using Core.IntegrationTests.Tests.ErrorBoundaryLab;

namespace Core.IntegrationTests.Tests.DeclarativeMappingLab;

/// <summary>
/// Dynamic Expresso outside task mappings (vnext #1229, "Dynamic Expresso outside task mappings" and
/// "Declarative blocks of a nested task" in the runtime's <c>docs/runtime/declarative-task-mappings.md</c>):
/// a <c>ScriptCode</c> with <c>location: "dynamicExpresso"</c> is an inline expression — no <c>.csx</c>, no Roslyn
/// compile — in a transition <c>timer</c>, a <c>resourceLock.keyExpression</c>, an automatic transition
/// <c>rule</c>, an AiAgent <c>prompt</c> and <c>decision.mapping</c>; and a CacheAside task shapes its source
/// task with the declarative <c>sourceInput</c> / <c>sourceOutput</c> blocks instead of a C# <c>sourceMapping</c>.
/// <para>
/// Flows: <c>core/Workflows/declarative-mapping-lab/dml-value-slots.json</c> (timer, lock, CacheAside, auto rule,
/// the <c>headers</c> variable)
/// and <c>dml-agent-slots.json</c> (agent on the runtime's deterministic <c>local</c> provider — no network, no key).
/// </para>
/// </summary>
public class ValueSlotExpressionTests : WorkflowTestBase
{
    private const string Flow = "dml-value-slots";
    private const string AgentFlow = "dml-agent-slots";

    private static readonly SemaphoreSlim AgentPublishGate = new(1, 1);
    private static bool _agentPublished;

    public ValueSlotExpressionTests(VNextTestEnvironment environment) : base(environment) { }

    // ── 1. transition timer ──────────────────────────────────────────────────

    /// <summary>
    /// A scheduled transition whose <c>timer</c> is an expression. Every supported result kind arms the timer
    /// where the value says, and the transition fires: <c>TimeSpan.FromSeconds(data.waitSeconds.AsDouble())</c>
    /// (a duration from data), <c>data.waitIso</c> (a JSON string read as ISO-8601 <c>PT3S</c>),
    /// <c>now.AddSeconds(3.0)</c> (an instant) — all 3 s — and <c>null</c> (immediate, which the runtime's
    /// <c>TimerSchedule.Immediate</c> arms at now + 2 s).
    /// </summary>
    [Theory]
    [InlineData("timespan", 3.0)]
    [InlineData("iso", 3.0)]
    [InlineData("datetime", 3.0)]
    [InlineData("null", 2.0)]
    public async Task TransitionTimer_ExpressionResult_ArmsAndFiresTheTimer(string kind, double expectedSeconds)
    {
        var instanceId = await StartAsync(Flow, new
        {
            key = NewKey($"dvs-timer-{kind}"),
            attributes = new { waitSeconds = 3, waitIso = "PT3S" }
        });
        await WaitUntilSettledAsync(Flow, instanceId);

        var before = DateTimeOffset.UtcNow;
        await RunAcceptedAsync(Flow, instanceId, $"wait-{kind}");

        var executeAtText = await GetScheduledExecuteAtAsync(Flow, instanceId, $"fire-{kind}");
        if (executeAtText is not null)
        {
            var executeAt = DateTimeOffset.Parse(executeAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var offset = (executeAt - before).TotalSeconds;
            Assert.True(offset >= expectedSeconds - 0.5 && offset <= expectedSeconds + 3,
                $"fire-{kind} armed {offset:0.00}s after the hop, expected ~{expectedSeconds}s (executeAtUtc {executeAtText})");
        }
        else
        {
            // Only a timer that has already fired leaves no scheduled entry.
            var (state, _) = await GetInstanceStateAsync(Flow, instanceId);
            Assert.True(state == "timer-fired", $"fire-{kind} is not armed and the instance is in '{state}'");
        }

        await WaitForInstanceStateAsync(Flow, instanceId, "timer-fired", timeout: TimeSpan.FromSeconds(60));
        var elapsed = (DateTimeOffset.UtcNow - before).TotalSeconds;
        Assert.True(elapsed >= expectedSeconds - 0.5, $"fire-{kind} fired after {elapsed:0.00}s, before its {expectedSeconds}s timer");
        await WaitUntilSettledAsync(Flow, instanceId);
        Assert.Equal("C", (await GetInstanceStateAsync(Flow, instanceId)).Status);
    }

    // ── 2. resource-lock key ─────────────────────────────────────────────────

    /// <summary>
    /// <c>resourceLock.keyExpression</c> = <c>"dml-vs:seat:" + context.Body["seat"]</c> (legacy <c>context</c>,
    /// the transition body). Same seat: the second instance's Acquire conflicts (<c>Execution:200010</c>) and the
    /// state's error boundary reroutes it to <c>seat-waiting</c>; a different seat is acquired at the same time; the
    /// Release transition (same expression) frees the key so a new instance takes it.
    /// </summary>
    [Fact]
    public async Task ResourceLockKeyExpression_SameKeyConflicts_DifferentKeyDoesNot_ReleaseFreesIt()
    {
        var seat = $"s-{Guid.NewGuid():N}";
        var holder = await StartIdleAsync("dvs-lock-holder");
        var loser = await StartIdleAsync("dvs-lock-loser");
        var other = await StartIdleAsync("dvs-lock-other");

        await RunAcceptedAsync(Flow, holder, "book-seat", body: new { seat });
        await WaitForInstanceStateAsync(Flow, holder, "seat-held");

        await RunAcceptedAsync(Flow, loser, "book-seat", body: new { seat });
        await WaitForInstanceStateAsync(Flow, loser, "seat-waiting");

        await RunAcceptedAsync(Flow, other, "book-seat", body: new { seat = seat + "-b" });
        await WaitForInstanceStateAsync(Flow, other, "seat-held");

        var (holderState, holderStatus) = await GetInstanceStateAsync(Flow, holder);
        Assert.Equal("seat-held", holderState);
        Assert.NotEqual("F", holderStatus);

        await RunAcceptedAsync(Flow, holder, "release-seat", body: new { seat });
        await WaitForInstanceStateAsync(Flow, holder, "seat-released");

        var next = await StartIdleAsync("dvs-lock-next");
        await RunAcceptedAsync(Flow, next, "book-seat", body: new { seat });
        await WaitForInstanceStateAsync(Flow, next, "seat-held");
        await AssertNotFaultedAsync(Flow, next);
    }

    /// <summary>
    /// The same lock with the standard slot variables (vnext: one variable set for every Dynamic Expresso slot):
    /// <c>"dml-vs:seat:" + data.seat</c> — the seat comes from the instance data the start wrote, not from the
    /// transition body (the hops carry none). Same seat conflicts and reroutes to <c>seat-waiting</c>, a different
    /// seat is acquired, Release with the same expression frees the key.
    /// </summary>
    [Fact]
    public async Task ResourceLockKeyExpression_StandardDataVariable_SameKeyConflicts_ReleaseFreesIt()
    {
        var seat = $"s-{Guid.NewGuid():N}";
        var holder = await StartWithSeatAsync("dvs-lockstd-holder", seat);
        var loser = await StartWithSeatAsync("dvs-lockstd-loser", seat);
        var other = await StartWithSeatAsync("dvs-lockstd-other", seat + "-b");

        await RunAcceptedAsync(Flow, holder, "book-seat-std");
        await WaitForInstanceStateAsync(Flow, holder, "seat-held");

        await RunAcceptedAsync(Flow, loser, "book-seat-std");
        await WaitForInstanceStateAsync(Flow, loser, "seat-waiting");

        await RunAcceptedAsync(Flow, other, "book-seat-std");
        await WaitForInstanceStateAsync(Flow, other, "seat-held");

        await RunAcceptedAsync(Flow, holder, "release-seat-std");
        await WaitForInstanceStateAsync(Flow, holder, "seat-released");

        var next = await StartWithSeatAsync("dvs-lockstd-next", seat);
        await RunAcceptedAsync(Flow, next, "book-seat-std");
        await WaitForInstanceStateAsync(Flow, next, "seat-held");
        await AssertNotFaultedAsync(Flow, next);
    }

    // ── 3. CacheAside sourceInput / sourceOutput ─────────────────────────────

    /// <summary>
    /// CacheAside <c>dml-cacheaside-echo</c> with source <c>dml-http-echo</c>, shaped by declarative blocks:
    /// <c>sourceInput</c> resolves the <c>API_BASEURL</c> URL from config and sets <c>$.body.customerId</c> and the
    /// <c>x-channel</c> header (the echo proves both); <c>sourceOutput</c> selects the echo into <c>$.echo</c> and
    /// sets <c>cachedAt</c> (<c>now</c>), <c>scorePlus</c> (<c>result.body.score + 1</c>) and <c>calledUrl</c> — that
    /// object, not the raw response, is cached. A second read of the same key is a hit: the same
    /// <c>cachedAt</c>, and MockLab saw exactly one call for this customer.
    /// </summary>
    [SkippableFact]
    public async Task CacheAside_DeclarativeSourceBlocks_ShapeTheCallAndTheCachedValue_SecondReadHits()
    {
        await SkipUnlessMockLabAsync();
        var customerId = $"c-{Guid.NewGuid():N}";
        var instanceId = await StartAsync(Flow, new { key = NewKey("dvs-cache"), attributes = new { customer = new { id = customerId } } });
        await WaitUntilSettledAsync(Flow, instanceId);

        await RunAcceptedAsync(Flow, instanceId, "cache-read");
        await AssertNotFaultedAsync(Flow, instanceId);
        var first = (await GetAttributesAsync(Flow, instanceId)).GetProperty("cache").GetProperty("latest").Clone();

        var echo = first.GetProperty("echo");
        Assert.Equal(customerId, echo.GetProperty("customerId").GetString()); // sourceInput set on $.body
        Assert.Equal("cache", echo.GetProperty("channel").GetString());      // sourceInput set on $.headers
        Assert.Equal("static", echo.GetProperty("source").GetString());      // the source task's static body kept
        Assert.Equal(88, first.GetProperty("scorePlus").GetInt32());          // sourceOutput: result.body.score + 1
        var calledUrl = first.GetProperty("calledUrl").GetString();
        Assert.DoesNotContain("API_BASEURL", calledUrl);
        Assert.EndsWith("/api/dml/echo", calledUrl);
        Assert.False(first.TryGetProperty("score", out _), "the raw response must not be cached, only the sourceOutput shape");
        Assert.False(first.TryGetProperty("level", out _), "the raw response must not be cached, only the sourceOutput shape");
        var cachedAt = first.GetProperty("cachedAt").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cachedAt));

        await RunAcceptedAsync(Flow, instanceId, "cache-read");
        await AssertNotFaultedAsync(Flow, instanceId);
        var second = (await GetAttributesAsync(Flow, instanceId)).GetProperty("cache").GetProperty("latest");
        Assert.Equal(cachedAt, second.GetProperty("cachedAt").GetString()); // a hit returns the value stamped at source time

        Assert.Equal(1, await CountMockLabCallsAsync(customerId));
    }

    /// <summary>
    /// CacheAside <c>dml-cacheaside-data-key</c>: the same task shape keyed by the standard <c>data</c> variable,
    /// <c>"dml-vs:ca-data:" + data.customer.id</c>. Two different instances with the same customer share one cache
    /// entry — the second instance's read is a hit (same <c>cachedAt</c>) and MockLab saw exactly one call — which a
    /// key over <c>context.Instance.Key</c> could not do.
    /// </summary>
    [SkippableFact]
    public async Task CacheAside_KeyFromDataVariable_IsSharedAcrossInstancesOfTheSameCustomer()
    {
        await SkipUnlessMockLabAsync();
        var customerId = $"c-{Guid.NewGuid():N}";
        var first = await StartAsync(Flow, new { key = NewKey("dvs-cachestd-a"), attributes = new { customer = new { id = customerId } } });
        var second = await StartAsync(Flow, new { key = NewKey("dvs-cachestd-b"), attributes = new { customer = new { id = customerId } } });
        await WaitUntilSettledAsync(Flow, first);
        await WaitUntilSettledAsync(Flow, second);

        await RunAcceptedAsync(Flow, first, "cache-read-std");
        await AssertNotFaultedAsync(Flow, first);
        var a = (await GetAttributesAsync(Flow, first)).GetProperty("cacheStd").GetProperty("latest").Clone();
        Assert.Equal(customerId, a.GetProperty("echo").GetProperty("customerId").GetString());
        var cachedAt = a.GetProperty("cachedAt").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cachedAt));

        await RunAcceptedAsync(Flow, second, "cache-read-std");
        await AssertNotFaultedAsync(Flow, second);
        var b = (await GetAttributesAsync(Flow, second)).GetProperty("cacheStd").GetProperty("latest");
        Assert.Equal(cachedAt, b.GetProperty("cachedAt").GetString()); // the other instance's entry: a hit

        Assert.Equal(1, await CountMockLabCallsAsync(customerId));
    }

    // ── 4. automatic transition rule ─────────────────────────────────────────

    /// <summary>
    /// Two automatic transitions of <c>routing</c> carry Dynamic Expresso rules over the legacy context:
    /// <c>context.Instance.Data["amount"].AsDouble() &gt; 1000</c> and <c>&lt;= 1000</c>. The amount arrives in the
    /// manual <c>route</c> transition's body; the auto step picks the matching branch in the same chain.
    /// </summary>
    [Theory]
    [InlineData(5000, "routed-large")]
    [InlineData(20, "routed-small")]
    public async Task AutoTransitionRule_DynamicExpresso_RoutesOnInstanceData(int amount, string expected)
    {
        var instanceId = await StartIdleAsync($"dvs-route-{amount}");

        await RunAcceptedAsync(Flow, instanceId, "route", body: new { amount });
        await WaitForInstanceStateAsync(Flow, instanceId, expected);
        await WaitUntilSettledAsync(Flow, instanceId);

        var (state, status) = await GetInstanceStateAsync(Flow, instanceId);
        Assert.Equal(expected, state);
        Assert.Equal("C", status);
    }

    /// <summary>
    /// The same routing with the standard slot variables: <c>data.amount.AsDouble() &gt; 1000 &amp;&amp;
    /// instance.currentState == "routing-std"</c> and <c>data.amount.AsDouble() &lt;= 1000 &amp;&amp; user.available</c>
    /// (the auto hop chained after a caller's manual transition has a caller). Both new-variable rules select.
    /// </summary>
    [Theory]
    [InlineData(5000, "routed-large")]
    [InlineData(20, "routed-small")]
    public async Task AutoTransitionRule_StandardVariables_RoutesOnData(int amount, string expected)
    {
        var instanceId = await StartIdleAsync($"dvs-routestd-{amount}");

        await RunAcceptedAsync(Flow, instanceId, "route-std", body: new { amount });
        await WaitForInstanceStateAsync(Flow, instanceId, expected);
        await WaitUntilSettledAsync(Flow, instanceId);

        var (state, status) = await GetInstanceStateAsync(Flow, instanceId);
        Assert.Equal(expected, state);
        Assert.Equal("C", status);
    }

    // ── 4b. `input` = the hop's request payload (uniform hop input) ──────────

    /// <summary>
    /// <c>input</c> is the raw request payload of THIS hop, whichever step reads it. <c>route-input</c> carries no
    /// task, so no task step builds the hop's script context before the automatic rules of <c>routing-input</c>
    /// (step 80): <c>input.lane.ToString() == "fast"</c> / <c>"slow"</c> still see the manual body and route. On
    /// <c>fast</c>, the automatic continuation hop that follows evaluates <c>input-fast</c>'s rules: <c>input == null</c>
    /// → <c>input-continuation-null</c>, <c>input != null</c> → <c>input-continuation-leaked</c> (a continuation carries no
    /// request payload — even though the body's <c>lane</c> was merged into the instance data).
    /// </summary>
    [Theory]
    [InlineData("fast", "input-continuation-null")]
    [InlineData("slow", "input-slow")]
    public async Task AutoTransitionRule_ReadsTheHopInput_WithoutTasks_AndNullOnTheContinuation(string lane, string expected)
    {
        var instanceId = await StartIdleAsync($"dvs-input-{lane}");

        await RunAcceptedAsync(Flow, instanceId, "route-input", body: new { lane });
        await WaitForInstanceStateAsync(Flow, instanceId, expected);
        await WaitUntilSettledAsync(Flow, instanceId);

        var (state, status) = await GetInstanceStateAsync(Flow, instanceId);
        Assert.Equal(expected, state);
        Assert.Equal("C", status);
        // The body went into the data too, so `data.lane` would not tell the hops apart — `input` does.
        Assert.Equal(lane, (await GetAttributesAsync(Flow, instanceId)).GetProperty("lane").GetString());
    }

    /// <summary>
    /// <c>resourceLock.keyExpression</c> = <c>"dml-vs:seat:" + input.seat</c>: the lock step (order 25) runs before
    /// any task step, yet <c>input</c> is the transition body. Same seat conflicts (<c>Execution:200010</c> →
    /// <c>seat-waiting</c>), and it is the SAME key as the legacy <c>context.Body["seat"]</c> expression: a
    /// <c>book-seat</c> holder blocks a <c>book-seat-input</c> request for that seat. A different seat is acquired;
    /// Release with the input expression frees the key.
    /// </summary>
    [Fact]
    public async Task ResourceLockKeyExpression_InputVariable_IsTheTransitionBody()
    {
        var seat = $"s-{Guid.NewGuid():N}";
        var holder = await StartIdleAsync("dvs-lockin-holder");
        var loser = await StartIdleAsync("dvs-lockin-loser");
        var legacyLoser = await StartIdleAsync("dvs-lockin-legacy");
        var other = await StartIdleAsync("dvs-lockin-other");

        await RunAcceptedAsync(Flow, holder, "book-seat-input", body: new { seat });
        await WaitForInstanceStateAsync(Flow, holder, "seat-held");

        await RunAcceptedAsync(Flow, loser, "book-seat-input", body: new { seat });
        await WaitForInstanceStateAsync(Flow, loser, "seat-waiting");

        await RunAcceptedAsync(Flow, legacyLoser, "book-seat", body: new { seat });
        await WaitForInstanceStateAsync(Flow, legacyLoser, "seat-waiting");

        await RunAcceptedAsync(Flow, other, "book-seat-input", body: new { seat = seat + "-b" });
        await WaitForInstanceStateAsync(Flow, other, "seat-held");

        await RunAcceptedAsync(Flow, holder, "release-seat-input", body: new { seat });
        await WaitForInstanceStateAsync(Flow, holder, "seat-released");

        var next = await StartIdleAsync("dvs-lockin-next");
        await RunAcceptedAsync(Flow, next, "book-seat-input", body: new { seat });
        await WaitForInstanceStateAsync(Flow, next, "seat-held");
        await AssertNotFaultedAsync(Flow, next);
    }

    // ── 4c. `headers` = the incoming request's headers ───────────────────────

    /// <summary>
    /// The <c>headers</c> variable (a read-only view over <c>context.Headers</c>) in an automatic rule: the manual
    /// <c>route-channel</c> request carries <c>x-channel</c>, and the rules of <c>routing-channel</c> read it in the
    /// same hop — <c>headers["x-channel"] == "web"</c> → <c>channel-web</c>; <c>!headers.has("X-Channel") ||
    /// headers["X-Channel"] != "web"</c> (case-insensitive name) → <c>channel-other</c>, both with another value and
    /// with no header at all.
    /// </summary>
    [Theory]
    [InlineData("web", "channel-web")]
    [InlineData("mobile", "channel-other")]
    [InlineData(null, "channel-other")]
    public async Task AutoTransitionRule_HeadersVariable_RoutesOnTheRequestHeader(string? channel, string expected)
    {
        var instanceId = await StartIdleAsync($"dvs-channel-{channel ?? "none"}");

        await RunWithHeadersAsync(instanceId, "route-channel", channel is null ? [] : [("x-channel", channel)]);
        await WaitForInstanceStateAsync(Flow, instanceId, expected);
        await WaitUntilSettledAsync(Flow, instanceId);

        var (state, status) = await GetInstanceStateAsync(Flow, instanceId);
        Assert.Equal(expected, state);
        Assert.Equal("C", status);
    }

    /// <summary>
    /// <c>resourceLock.keyExpression</c> = <c>"dml-vs:seat:" + headers["x-seat"]</c>: the key comes from a request
    /// header, not the body (the hops carry none). Same header value conflicts (<c>Execution:200010</c> →
    /// <c>seat-waiting</c>) — and it is the SAME key as the body-based <c>book-seat-input</c> expression; a different
    /// value is acquired; Release with the header expression frees the key.
    /// </summary>
    [Fact]
    public async Task ResourceLockKeyExpression_HeadersVariable_KeysOnTheRequestHeader()
    {
        var seat = $"s-{Guid.NewGuid():N}";
        var holder = await StartIdleAsync("dvs-lockhdr-holder");
        var loser = await StartIdleAsync("dvs-lockhdr-loser");
        var bodyLoser = await StartIdleAsync("dvs-lockhdr-body");
        var other = await StartIdleAsync("dvs-lockhdr-other");

        await RunWithHeadersAsync(holder, "book-seat-header", [("x-seat", seat)]);
        await WaitForInstanceStateAsync(Flow, holder, "seat-held");

        await RunWithHeadersAsync(loser, "book-seat-header", [("x-seat", seat)]);
        await WaitForInstanceStateAsync(Flow, loser, "seat-waiting");

        await RunAcceptedAsync(Flow, bodyLoser, "book-seat-input", body: new { seat });
        await WaitForInstanceStateAsync(Flow, bodyLoser, "seat-waiting");

        await RunWithHeadersAsync(other, "book-seat-header", [("x-seat", seat + "-b")]);
        await WaitForInstanceStateAsync(Flow, other, "seat-held");

        await RunWithHeadersAsync(holder, "release-seat-header", [("x-seat", seat)]);
        await WaitForInstanceStateAsync(Flow, holder, "seat-released");

        var next = await StartIdleAsync("dvs-lockhdr-next");
        await RunWithHeadersAsync(next, "book-seat-header", [("x-seat", seat)]);
        await WaitForInstanceStateAsync(Flow, next, "seat-held");
        await AssertNotFaultedAsync(Flow, next);
    }

    // ── 5. agent prompt / decision ───────────────────────────────────────────

    /// <summary>
    /// AiAgent <c>decision.mode: script</c> with a Dynamic Expresso <c>decision.mapping</c> reading <c>data</c>,
    /// <c>outcome</c> and <c>candidates</c>: <c>pick = is-fraud</c> → that candidate; <c>pick = by-output</c> → derived
    /// from <c>outcome.output.category</c> (the local provider always answers <c>question</c>) → <c>is-question</c>;
    /// anything else → <c>null</c> → the fallback.
    /// </summary>
    [Theory]
    [InlineData("is-fraud", "escalated", "is-fraud", "Script")]
    [InlineData("by-output", "answered", "is-question", "Script")]
    [InlineData("no-such", "failed", "classify-failed", "Fallback")]
    public async Task AgentDecisionExpression_ChoosesTheTransition(string pick, string state, string decision, string source)
    {
        await EnsureAgentPublishedAsync();
        var instanceId = await StartAsync(AgentFlow, new { key = NewKey("dvs-agent"), attributes = new { message = "hello", pick } });

        await WaitForInstanceStateAsync(AgentFlow, instanceId, state, timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(AgentFlow, instanceId);

        var agent = (await GetAttributesAsync(AgentFlow, instanceId)).GetProperty("triage").GetProperty("$agent");
        Assert.Equal("Completed", agent.GetProperty("status").GetString());
        Assert.Equal(decision, agent.GetProperty("decision").GetString());
        Assert.Equal(source, agent.GetProperty("decisionSource").GetString());
    }

    /// <summary>
    /// The prompt is <c>"&lt;message&gt;" + data.message.ToString() + "&lt;/message&gt;"</c>. The local provider counts
    /// prompt tokens as characters / 4, so a 4000-character message must cost about 1000 more tokens than a short
    /// one — the expression really read <c>data.message</c> into the prompt. (The run row's <c>promptHash</c> is the
    /// exact proof; it is not on the read API, see the README.)
    /// </summary>
    [Fact]
    public async Task AgentPromptExpression_BuildsThePromptFromData()
    {
        await EnsureAgentPublishedAsync();
        var shortTokens = await RunAgentAndReadTokensAsync("hi");
        var longTokens = await RunAgentAndReadTokensAsync(new string('x', 4000));

        Assert.True(longTokens - shortTokens >= 900,
            $"a 4000-char message must reach the prompt: short={shortTokens} tokens, long={longTokens} tokens");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string NewKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<string> StartIdleAsync(string prefix)
    {
        var instanceId = await StartAsync(Flow, new { key = NewKey(prefix), attributes = new { } });
        await WaitUntilSettledAsync(Flow, instanceId);
        return instanceId;
    }

    /// <summary><see cref="WorkflowTestBase.RunAcceptedAsync"/> with extra request headers beside the caller headers.</summary>
    private async Task RunWithHeadersAsync(string instanceId, string transitionKey, (string Name, string Value)[] extra)
    {
        var headers = Headers();
        foreach (var (name, value) in extra) headers[name] = value;
        var url = $"api/v1/{TestDomain.Name}/workflows/{Flow}/instances/{instanceId}/transitions/{transitionKey}?sync=false";

        var (status, body) = await SendRawAsync(HttpMethod.Patch, url, new { }, headers);

        Assert.True((int)status < 400, $"'{transitionKey}' was refused with {(int)status}: {body}");
        await WaitUntilSettledAsync(Flow, instanceId);
    }

    private async Task<string> StartWithSeatAsync(string prefix, string seat)
    {
        var instanceId = await StartAsync(Flow, new { key = NewKey(prefix), attributes = new { seat } });
        await WaitUntilSettledAsync(Flow, instanceId);
        return instanceId;
    }

    private async Task<int> RunAgentAndReadTokensAsync(string message)
    {
        var instanceId = await StartAsync(AgentFlow, new { key = NewKey("dvs-prompt"), attributes = new { message, pick = "is-fraud" } });
        await WaitForInstanceStateAsync(AgentFlow, instanceId, "escalated", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(AgentFlow, instanceId);

        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{AgentFlow}/instances/{instanceId}/functions/agent-runs", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"agent-runs answered {(int)status}: {raw}");
        var run = Assert.Single(JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray());
        return run.GetProperty("totalTokens").GetInt32();
    }

    private static async Task SkipUnlessMockLabAsync()
    {
        using var mocklab = new MockLabAdminClient();
        Skip.If(!await mocklab.IsUpAsync(),
            $"MockLab is not reachable at {mocklab.BaseUrl} — start it with `docker compose up -d` in the repo root.");
    }

    /// <summary>MockLab requests to <c>/api/dml/echo</c> whose body carries this customer id.</summary>
    private static async Task<int> CountMockLabCallsAsync(string customerId)
    {
        using var mocklab = new MockLabAdminClient();
        using var http = new HttpClient();
        var raw = await http.GetStringAsync($"{mocklab.BaseUrl.TrimEnd('/')}/_admin/logs?limit=500");
        return JsonDocument.Parse(raw).RootElement.GetProperty("data").EnumerateArray()
            .Count(e => e.GetProperty("route").GetString() == "/api/dml/echo"
                        && (e.GetProperty("requestBody").GetString() ?? string.Empty).Contains(customerId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Same as <c>AiAgentLabTests</c>: the SDK does not publish <c>sys-agents</c> or agents, so the
    /// <c>sys-agents</c> flow fixture and the lab agent are published once per run ("already" is fine).
    /// </summary>
    private async Task EnsureAgentPublishedAsync()
    {
        await AgentPublishGate.WaitAsync();
        try
        {
            if (_agentPublished) return;
            var root = RepoRoot();
            await PublishAsync(Path.Combine(root, "tests", "Core.IntegrationTests", "Tests", "AiAgentLab", "fixtures", "sys-agents.json"));
            await PublishAsync(Path.Combine(root, "core", "Agents", "ai-agent-lab", "ai-triage-agent.json"));
            await PublishAsync(Path.Combine(root, "core", "Workflows", "declarative-mapping-lab", $"{AgentFlow}.json"));
            _agentPublished = true;
        }
        finally
        {
            AgentPublishGate.Release();
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
