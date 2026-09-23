using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Core.IntegrationTests.Tests.CrossDomainLab;

namespace Core.IntegrationTests.Tests.HumanTaskChain;

/// <summary>
/// The <c>human-task</c> domain function answers "which human tasks is this caller expected to act
/// on". These pin the two things that make that answer correct when the work is not where the
/// client is looking: the row is addressed by the ROOT, and its authorization and text come from
/// the LEAF — however many SubFlow levels and domain boundaries away that leaf is.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: the function used to descend exactly ONE level, resolve the child's definition
/// against the CALLER's domain instead of the correlation's, and read the title from the root. So a
/// task two levels down was authorized against the wrong state, and a cross-domain child resolved
/// to nothing and silently removed its whole instance from the list.
/// </para>
/// <para>
/// The chain's depth is data: the start payload's <c>hops</c> decides which level ends up holding
/// the task, so one definition family covers all three shapes. See
/// <c>api-tests/human-task-chain/build-human-task-chain.py</c>.
/// </para>
/// </remarks>
public class HumanTaskFunctionTests(VNextTestEnvironment environment, CrossDomainLabFixture lab, HumanTaskChainFixture credit)
    : WorkflowTestBase(environment), IClassFixture<CrossDomainLabFixture>, IClassFixture<HumanTaskChainFixture>
{
    private const string Root = "ht-a";
    private const string ApproverRole = "ht-approver";

    /// <summary>One row of the human-task response.</summary>
    private sealed record HumanTaskRow(string InstanceId, string Id, string Workflow, string Title, string Description);

    /// <summary>
    /// Reads the list. <paramref name="fresh"/> sends the cache-override header, which is what a
    /// client uses at a moment it cannot tolerate the response cache's TTL — and what a test
    /// polling for a change must use, since the cache would otherwise serve the pre-change answer
    /// for a full TTL and the wait would time out against a stale list rather than a wrong one.
    /// </summary>
    private async Task<IReadOnlyList<HumanTaskRow>> ListHumanTasksAsync(
        string? roles = ApproverRole, bool fresh = false)
    {
        var headers = Headers(roles);
        if (fresh) headers["X-VNext-Cache-Override"] = "true";

        var (status, body) = await SendRawAsync(
            HttpMethod.Get, "api/v1/core/functions/human-task", body: null, headers: headers);

        Assert.True(status == HttpStatusCode.OK, $"human-task function failed: {status} {body}");

        return ParseHumanTasks(body);
    }

    /// <summary>
    /// The same read against ANOTHER domain's orchestrator. morph-idm fans out over every
    /// registered domain and merges the answers, so a row that only exists in partner's own list is
    /// a row the aggregator will show — a SubProcess is listed by the domain that OWNS it, never by
    /// the domain its parent lives in.
    /// </summary>
    private static async Task<IReadOnlyList<HumanTaskRow>> ListHumanTasksAsync(
        string baseUrl, string domain, string? roles = ApproverRole, bool fresh = false)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/{domain}/functions/human-task");

        var headers = Headers(roles);
        if (fresh) headers["X-VNext-Cache-Override"] = "true";
        foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"human-task function on {domain} failed: {response.StatusCode} {body}");

        return ParseHumanTasks(body);
    }

    private static IReadOnlyList<HumanTaskRow> ParseHumanTasks(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateArray()
            .Select(row => new HumanTaskRow(
                row.GetProperty("instanceId").GetString() ?? string.Empty,
                row.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                row.GetProperty("workflow").GetString() ?? string.Empty,
                row.GetProperty("title").GetString() ?? string.Empty,
                row.GetProperty("description").GetString() ?? string.Empty))
            .ToList();
    }

    /// <summary>
    /// Starts the root through the raw client WITHOUT <c>sync=true</c>. The SDK client hard-codes
    /// the synchronous mode, and the synchronous mode is the one a state-level SubProcess cannot
    /// survive today (see the remarks on the SubProcess test).
    /// </summary>
    private async Task<string> StartAsyncAndGetIdAsync(object attributes)
    {
        var (status, body) = await SendRawAsync(
            HttpMethod.Post,
            $"api/v1/core/workflows/{Root}/instances/start",
            new { key = $"ht-spawn-{Guid.NewGuid():N}", attributes },
            Headers(ApproverRole));

        Assert.True(status == HttpStatusCode.Accepted || status == HttpStatusCode.OK,
            $"async start failed: {status} {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Calls a built-in instance function on whichever domain owns the flow.</summary>
    private static async Task<(HttpStatusCode Status, string Body)> FunctionAsync(
        string baseUrl, string domain, string flow, string instanceId, string function, string roles,
        string? query = null)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/v1/{domain}/workflows/{flow}/instances/{instanceId}/functions/{function}{query}");
        foreach (var (key, value) in Headers(roles)) request.Headers.TryAddWithoutValidation(key, value);

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>`authorize`'s verdict.</summary>
    /// <remarks>
    /// The verdict is in the BODY on both statuses: a refusal answers <c>403</c> with
    /// <c>{"allowed":false}</c>, not an empty error. Reading only the 200 would silently turn every
    /// refusal into "no answer" and make a denial assertion pass for the wrong reason.
    /// </remarks>
    private static async Task<bool> AuthorizeAsync(
        string baseUrl, string domain, string flow, string instanceId, string roles, string query)
    {
        var (status, body) = await FunctionAsync(baseUrl, domain, flow, instanceId, "authorize", roles, query);
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Forbidden,
            $"authorize answered {status}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("allowed").GetBoolean();
    }

    /// <summary>Whether the state function offers <paramref name="transitionKey"/>, or null on a 403.</summary>
    private static async Task<bool?> OffersAsync(
        string baseUrl, string domain, string flow, string instanceId, string roles, string transitionKey)
    {
        var (status, body) = await FunctionAsync(baseUrl, domain, flow, instanceId, "state", roles);
        if (status == HttpStatusCode.Forbidden) return null;
        Assert.Equal(HttpStatusCode.OK, status);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("transitions").EnumerateArray()
            .Any(t => t.GetProperty("name").GetString() == transitionKey);
    }

    /// <summary>The instance of <paramref name="targetFlow"/> within a chain rooted at <paramref name="rootId"/>.</summary>
    /// <remarks>
    /// Walked from the root rather than assumed, because the chain's depth is data: the instance ids
    /// are only discoverable through each level's active correlation. The walk crosses into partner,
    /// so each hop is made against the domain that owns the level.
    /// </remarks>
    private async Task<string> ResolveLevelAsync(string rootId, string targetFlow)
    {
        var flow = Root;
        var id = rootId;

        for (var depth = 0; depth < 10 && flow != targetFlow; depth++)
        {
            var (baseUrl, domain) = EndpointOf(flow);
            using var document = await StateFunctionAsync(baseUrl, domain, flow, id, ApproverRole);

            var correlations = document.RootElement.GetProperty("activeCorrelations");
            Assert.True(correlations.GetArrayLength() > 0, $"{flow}/{id} has no active subflow to walk into");

            flow = correlations[0].GetProperty("subFlowName").GetString()!;
            id = correlations[0].GetProperty("subFlowInstanceId").GetString()!;
        }

        Assert.Equal(targetFlow, flow);
        return id;
    }

    /// <summary>The transition keys a state function offers a caller — the actionability surface.</summary>
    private static async Task<IReadOnlyList<string>> AvailableTransitionsAsync(
        string baseUrl, string domain, string flow, string instanceId, string roles)
    {
        using var document = await StateFunctionAsync(baseUrl, domain, flow, instanceId, roles);
        return [.. document.RootElement.GetProperty("transitions").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString() ?? string.Empty)];
    }

    /// <summary>Reads a state function from whichever domain owns the flow.</summary>
    private static async Task<JsonDocument> StateFunctionAsync(
        string baseUrl, string domain, string flow, string instanceId, string roles)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"api/v1/{domain}/workflows/{flow}/instances/{instanceId}/functions/state");
        foreach (var (key, value) in Headers(roles)) request.Headers.TryAddWithoutValidation(key, value);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"state function failed for {domain}/{flow}/{instanceId} as [{roles}]: {response.StatusCode} {body}");

        return JsonDocument.Parse(body);
    }

    /// <summary>Base url and domain name for a flow of the chain.</summary>
    private (string BaseUrl, string Domain) EndpointOf(string flow) => flow switch
    {
        "ht-d" => (lab.PartnerBaseUrl!, "partner"),
        "ht-e" or "ht-f" => (credit.CreditBaseUrl!, "credit"),
        _ => (CoreBaseUrl, "core")
    };

    private static string CoreBaseUrl =>
        System.Environment.GetEnvironmentVariable("VNEXT_BASE_URL")?.TrimEnd('/') ?? "http://localhost:4201";

    /// <summary>
    /// Starts a chain and waits until it has come to rest on a human state somewhere below, which
    /// is exactly when the root becomes listable.
    /// </summary>
    private async Task<string> StartChainAsync(int hops, string? visibleTo = null)
    {
        var instanceId = await StartAsync(Root, new
        {
            hops,
            testId = Guid.NewGuid().ToString("N"),
            humanTask = new { title = "HT-A step", description = "HT-A step description" }
        }, ApproverRole);

        await AssertNotFaultedAsync(Root, instanceId, ApproverRole);

        // Waited for as the role that can actually SEE it. A level whose queryRoles the parent
        // overrode is invisible to the broad role by design, so polling with that role here would
        // burn the whole timeout on a list that is correctly not showing it.
        await WaitUntilAsync(
            async () => (await ListHumanTasksAsync(roles: visibleTo ?? ApproverRole, fresh: true))
                .Any(row => row.Id == instanceId),
            $"chain {instanceId} (hops={hops}) never surfaced as a human task",
            TimeSpan.FromMinutes(2));

        return instanceId;
    }

    /// <summary>
    /// Senaryo 1 — A → B → C, all in core. The leaf is C, three levels below the row the client sees.
    /// </summary>
    [SkippableFact]
    public async Task Scenario1_ThreeLevelsInOneDomain_ListsTheRootWithTheLeafsText()
    {
        var instanceId = await StartChainAsync(hops: 2);

        var row = (await ListHumanTasksAsync(fresh: true)).Single(r => r.Id == instanceId);

        // The row is addressed by the root, which is all the client knows.
        Assert.Equal(Root, row.Workflow);
        // The text must come from the leaf, not from the root's own data.
        Assert.Equal("HT-C step", row.Title);
        Assert.Equal("HT-C step description", row.Description);
    }

    /// <summary>
    /// Senaryo 2 — A → B → C in core, then D in partner. One boundary; before the fix this instance
    /// left the list entirely, because the child's definition was resolved against core.
    /// </summary>
    [SkippableFact]
    public async Task Scenario2_CrossingIntoPartner_StillListsTheRootWithTheLeafsText()
    {
        Skip.If(lab.PartnerBaseUrl is null, "VNEXT_CREDIT/PARTNER lab not configured — run labs/cross-domain/lab.sh up");

        var instanceId = await StartChainAsync(hops: 3);

        var row = (await ListHumanTasksAsync(fresh: true)).Single(r => r.Id == instanceId);

        Assert.Equal(Root, row.Workflow);
        // The leaf lives in partner; its text is what the banker must read.
        Assert.Equal("HT-D step", row.Title);
    }

    /// <summary>
    /// Senaryo 3 — A → B → C (core) → D (partner) → E → F (credit). Two boundaries, and the second
    /// one is crossed by the PARTNER runtime: core never talks to credit.
    /// </summary>
    [SkippableFact]
    public async Task Scenario3_TwoBoundaries_ResolvesAllTheWayToTheCreditLeaf()
    {
        Skip.If(lab.PartnerBaseUrl is null, "partner domain not configured — run labs/cross-domain/lab.sh up");
        Skip.If(credit.CreditBaseUrl is null, "credit domain not configured — run labs/cross-domain/lab.sh up");

        var instanceId = await StartChainAsync(hops: 5);

        var row = (await ListHumanTasksAsync(fresh: true)).Single(r => r.Id == instanceId);

        // Six levels and two domains down, the row is still the root's.
        Assert.Equal(Root, row.Workflow);
        Assert.Equal("HT-F step", row.Title);
    }

    /// <summary>
    /// A root resting in its OWN human state is its own leaf — the simplest shape, and the one an
    /// implementation that always descends would get wrong.
    /// </summary>
    [SkippableFact]
    public async Task ARootRestingInItsOwnHumanStateIsListedWithItsOwnText()
    {
        var instanceId = await StartChainAsync(hops: 0);

        var row = (await ListHumanTasksAsync(fresh: true)).Single(r => r.Id == instanceId);

        Assert.Equal(Root, row.Workflow);
        Assert.Equal("HT-A step", row.Title);
    }

    /// <summary>
    /// One workflow declares several human states, and two instances of it can be waiting in
    /// different ones at the same time — one in the root's own, one several levels down. Both belong
    /// in the list, each carrying the text of the state ITS work is actually parked in.
    /// </summary>
    /// <remarks>
    /// This is the shape that makes "where is the task" a per-instance question rather than a
    /// per-definition one: the definition says nothing about which of its human states matters, and
    /// only the instance's effective position does.
    /// </remarks>
    [SkippableFact]
    public async Task TwoInstancesOfOneFlowAreListedByWhereEachIsEffectivelyWaiting()
    {
        var atOwnHumanState = await StartChainAsync(hops: 0);
        var waitingTwoLevelsDown = await StartChainAsync(hops: 2);

        var rows = await ListHumanTasksAsync(fresh: true);

        var shallow = rows.Single(r => r.Id == atOwnHumanState);
        var deep = rows.Single(r => r.Id == waitingTwoLevelsDown);

        // Same workflow, same root identity shape — different effective positions, different text.
        Assert.Equal(Root, shallow.Workflow);
        Assert.Equal(Root, deep.Workflow);
        Assert.Equal("HT-A step", shallow.Title);
        Assert.Equal("HT-C step", deep.Title);
        Assert.NotEqual(shallow.Id, deep.Id);
    }

    /// <summary>
    /// A SubProcess is an independent unit of work, so it gets its OWN row — addressed by its own
    /// id, not by the business key it inherited from the case that spawned it — and it descends
    /// through its own SubFlows exactly like a root does.
    /// </summary>
    /// <remarks>
    /// The root spawns <c>ht-d</c> as a SubProcess and carries straight on to its own human state:
    /// nothing waits for the child and nothing projects its state upward. Two independent rows must
    /// therefore appear. Before <c>Type IN ('R','P')</c> the SubProcess branch was invisible
    /// entirely, and while the row carried <c>Key</c> it collided with the root's and led a client
    /// following it to the wrong instance.
    /// </remarks>
    [SkippableFact]
    public async Task ASpawnedSubProcessIsListedOnItsOwnAndDescendsLikeARoot()
    {
        Skip.If(lab.PartnerBaseUrl is null, "partner domain not configured — run labs/cross-domain/lab.sh up");
        Skip.If(credit.CreditBaseUrl is null, "credit domain not configured — run labs/cross-domain/lab.sh up");

        // hops = 0 keeps the ROOT in its own human state; processHops = 2 sends the spawned
        // SubProcess down two more SubFlow levels, so its leaf is ht-f in credit.
        //
        // Started ASYNC on purpose. A state-level SubProcess followed by an automatic transition
        // cannot be started synchronously today: the post-commit ContinueParent continuation
        // re-enters the pipeline with IsPreReserved = false while the instance is still Busy from
        // the stage that continuation belongs to, so admission rejects it with
        // "conflict.Instance:100031". The async path only escapes it because a job re-entry sets
        // IsPreReserved independently. That is a runtime defect in the transition pipeline, not in
        // the human-task function — see TEST-SCENARIOS.md § Bilinen Kapsam Açıkları.
        var rootId = await StartAsyncAndGetIdAsync(new
        {
            mode = "process",
            hops = 0,
            processHops = 2,
            testId = Guid.NewGuid().ToString("N"),
            humanTask = new { title = "HT-A step", description = "HT-A step description" }
        });

        await AssertNotFaultedAsync(Root, rootId, ApproverRole);

        // The root is core's row, under its own business key, resting in its OWN human state.
        await WaitUntilAsync(
            async () => (await ListHumanTasksAsync(fresh: true)).Any(row => row.Id == rootId),
            $"root {rootId} never surfaced in core's human-task list",
            TimeSpan.FromMinutes(2));

        // The SubProcess is PARTNER's row — it is an independent flow, so the domain that owns it
        // lists it, and it descends through its own SubFlows into credit exactly like a root would.
        HumanTaskRow? spawned = null;
        await WaitUntilAsync(
            async () =>
            {
                var partnerRows = await ListHumanTasksAsync(lab.PartnerBaseUrl!, "partner", fresh: true);
                spawned = partnerRows.FirstOrDefault(
                    row => row.Workflow == "ht-d" && row.Title == "HT-F step");
                return spawned is not null;
            },
            "the spawned SubProcess never surfaced in partner's list with its leaf's text",
            TimeSpan.FromMinutes(2));

        var root = (await ListHumanTasksAsync(fresh: true)).Single(r => r.Id == rootId);

        Assert.Equal(Root, root.Workflow);
        Assert.Equal("HT-A step", root.Title);

        // The identity rule this test exists for: a SubProcess inherits its parent's business Key,
        // so addressing it by Key would send a client to the ROOT. It is addressed by its own id.
        Assert.Equal(spawned!.Id, spawned.InstanceId);
        Assert.NotEqual(root.InstanceId, spawned.InstanceId);
        Assert.NotEqual(root.Id, spawned.Id);

        // ...and it reached that text by descending its own two SubFlow levels into credit.
        Assert.Equal("HT-F step", spawned.Title);
    }

    /// <summary>
    /// The authorization decision is the LEAF's. A caller holding none of the leaf's grants must not
    /// see the task — proving the filter did not fall back to the root's state, which would have
    /// been evaluated against a different transition set.
    /// </summary>
    [SkippableFact]
    public async Task ACallerWithoutTheLeafsRoleDoesNotSeeTheTask()
    {
        var instanceId = await StartChainAsync(hops: 2);

        var rows = await ListHumanTasksAsync(roles: "some.other.role", fresh: true);

        Assert.DoesNotContain(rows, row => row.Id == instanceId);
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Authorization — the gate is the LEAF STATE's queryRoles
    //
    // The list answers "which human tasks am I responsible for?" — a VISIBILITY question, so it is
    // decided by queryRoles, not by whether some transition would admit the caller. Which button is
    // offered is settled later, when the client opens the instance and the state function runs.
    //
    // A wrong answer here is not a slow list, it is a banker seeing another banker's case — and the
    // failure is invisible in the response by construction, because an unauthorized row is simply
    // absent. Every test below is therefore a PAIR: something the caller must see and something the
    // same caller must not, from the same data in the same call. A test that only asserts absence
    // cannot tell "correctly filtered" from "the descent dropped it", and that is exactly how this
    // scenario's first negative test turned out to be vacuous.
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>queryRoles</c> are read from the state the instance is EFFECTIVELY in, not from the
    /// flow the row is addressed by. Both chains below are rooted in <c>ht-a</c> and differ only in
    /// where they came to rest, and a caller holding just one level's role separates them.
    /// </summary>
    /// <remarks>
    /// The positive half is what makes this a real test. Asserting only that the deep chain is
    /// hidden would pass just as well if the descent had failed, the leaf had been misresolved, or
    /// the row had never been listed at all — every one of which looks identical to "filtered".
    /// The leaf here is <c>ht-f</c> in <b>credit</b>, two domain boundaries away, so it also pins
    /// that the decision was taken in the domain that owns the leaf's definition.
    /// </remarks>
    [SkippableFact]
    public async Task OnlyTheLeafsOwnRoleGrantsTheTaskAndItGrantsNoOther()
    {
        Skip.If(lab.PartnerBaseUrl is null, "partner domain not configured — run labs/cross-domain/lab.sh up");
        Skip.If(credit.CreditBaseUrl is null, "credit domain not configured — run labs/cross-domain/lab.sh up");

        var restsInCredit = await StartChainAsync(hops: 5);   // leaf = ht-f (credit)
        var restsInCore = await StartChainAsync(hops: 2);     // leaf = ht-c (core)

        var rows = await ListHumanTasksAsync(roles: "ht-f-approver", fresh: true);

        Assert.Contains(rows, row => row.Id == restsInCredit);
        Assert.DoesNotContain(rows, row => row.Id == restsInCore);

        // ...and the discrimination is the caller's, not the data's: ht-approver is granted at every
        // level, so the same two instances are both visible to it.
        var broad = await ListHumanTasksAsync(roles: ApproverRole, fresh: true);
        Assert.Contains(broad, row => row.Id == restsInCredit);
        Assert.Contains(broad, row => row.Id == restsInCore);
    }

    /// <summary>
    /// A DENY grant refuses, whatever else the caller carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The composition is <c>AllowGroup AND DenyGroup</c>: allows OR among themselves, denies AND
    /// among themselves, and the deny group is evaluated first. One breached deny refuses outright —
    /// an allowed role no longer buys a denied one back.
    /// </para>
    /// <para>
    /// This test previously pinned the opposite, because the rule used to be applied per caller role
    /// inside a loop that returned on the first role that was allowed: a deny for role B was never
    /// reached once role A matched an allow, and <c>[approver, blocked]</c> passed. That made a deny
    /// grant unusable as a block, which is what the committee changed. The assertions below are the
    /// diff.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task ADenyGrantRefusesWhateverElseTheCallerCarries()
    {
        var instanceId = await StartChainAsync(hops: 2);

        // Allowed: ht-approver is granted by ht-c-human's queryRoles.
        var allowed = await ListHumanTasksAsync(roles: ApproverRole, fresh: true);
        Assert.Contains(allowed, row => row.Id == instanceId);

        // Refused: the denied role alone.
        var deniedAlone = await ListHumanTasksAsync(roles: "ht-blocked", fresh: true);
        Assert.DoesNotContain(deniedAlone, row => row.Id == instanceId);

        // Refused: the denied role BESIDE an allowed one. This is the cell that moved.
        var both = await ListHumanTasksAsync(roles: $"{ApproverRole},ht-blocked", fresh: true);
        Assert.DoesNotContain(both, row => row.Id == instanceId);

        // ...and order does not matter, which is what "the deny group is evaluated first" buys.
        var reversed = await ListHumanTasksAsync(roles: $"ht-blocked,{ApproverRole}", fresh: true);
        Assert.DoesNotContain(reversed, row => row.Id == instanceId);
    }

    /// <summary>
    /// The response cache must never serve one caller's authorized list to another.
    /// </summary>
    /// <remarks>
    /// Deliberately WITHOUT the cache-override header — every other test in this class sends it, so
    /// none of them exercises the cached path at all, and this is the one place where a cache-key
    /// mistake becomes a data-leak rather than a stale answer. The first call populates the entry
    /// for the broad role; the second, inside the TTL, must build its own rather than be served
    /// that one. A key that did not cover caller roles would hand the second caller a row it has no
    /// grant for.
    /// </remarks>
    [SkippableFact]
    public async Task TheResponseCacheDoesNotServeOneCallersListToAnother()
    {
        var instanceId = await StartChainAsync(hops: 2);

        var warmed = await ListHumanTasksAsync(roles: ApproverRole);
        Assert.Contains(warmed, row => row.Id == instanceId);

        // Same endpoint, same instant, different caller. Within the TTL, so a role-blind key would
        // hit the entry the line above just wrote.
        var other = await ListHumanTasksAsync(roles: "some.other.role");
        Assert.DoesNotContain(other, row => row.Id == instanceId);

        // And the first caller still sees it — proving the second call did not merely evict or
        // poison the entry.
        var again = await ListHumanTasksAsync(roles: ApproverRole);
        Assert.Contains(again, row => row.Id == instanceId);
    }

    /// <summary>
    /// A parent's <c>subFlow.overrides.states</c> narrows the CHILD's visibility, and that narrowing
    /// is honoured at the leaf.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SubflowStarter</c> stamps the map onto the child at start as
    /// <c>subflow.state_role_overrides</c>. Until this change nothing in the runtime read it — a
    /// dead write — because the only reader took the parent's definition and needed an active
    /// SubFlow correlation, which a leaf by definition does not have. So a parent that narrowed a
    /// child's visibility had that narrowing silently discarded exactly where it mattered.
    /// </para>
    /// <para>
    /// <c>ht-a</c>'s subflow state overrides <c>ht-b-human</c>'s queryRoles to
    /// <c>xd-override-only</c>. A chain resting on <c>ht-b</c> (<c>hops = 1</c>) is therefore visible
    /// to that role and NOT to <c>ht-approver</c>, which <c>ht-b</c>'s own definition grants — the
    /// override replaces, it does not merge. A chain resting one level deeper is untouched by it,
    /// which is what proves the map was applied per state rather than per instance.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task AParentsStampedStateOverrideNarrowsTheChildsVisibility()
    {
        var overridden = await StartChainAsync(hops: 4, visibleTo: "xd-override-only");   // leaf = ht-e, overridden by ht-d
        var untouched = await StartChainAsync(hops: 2);   // leaf = ht-c, not overridden

        var byOverride = await ListHumanTasksAsync(roles: "xd-override-only", fresh: true);
        Assert.Contains(byOverride, row => row.Id == overridden);
        Assert.DoesNotContain(byOverride, row => row.Id == untouched);

        // ht-b's OWN queryRoles grant ht-approver; the parent's override replaced them.
        var byOwnRole = await ListHumanTasksAsync(roles: ApproverRole, fresh: true);
        Assert.DoesNotContain(byOwnRole, row => row.Id == overridden);
        // ...and the level the parent did not override still answers to its own grants.
        Assert.Contains(byOwnRole, row => row.Id == untouched);
    }

    /// <summary>
    /// A DENY carried by the stamped override refuses, and it refuses a caller whose other role the
    /// override allows.
    /// </summary>
    [SkippableFact]
    public async Task ADenyInTheStampedStateOverrideRefuses()
    {
        var overridden = await StartChainAsync(hops: 4, visibleTo: "xd-override-only");

        Assert.Contains(
            await ListHumanTasksAsync(roles: "xd-override-only", fresh: true),
            row => row.Id == overridden);

        Assert.DoesNotContain(
            await ListHumanTasksAsync(roles: "xd-override-only,ht-blocked", fresh: true),
            row => row.Id == overridden);
    }

    /// <summary>
    /// The parent's <c>transitions</c> override still governs what the client is OFFERED, which is
    /// the half the list no longer decides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted through the OVERRIDING PARENT's state function, because that is the path the override
    /// exists for: it is a parent-side narrowing, applied while a client reaches the child through
    /// the parent that declared it. Going straight at the child answers 403 from the child's own
    /// <c>queryRoles</c>, which is correct and is a different question.
    /// </para>
    /// <para>
    /// A pair, as everywhere else here. <c>ht-d</c> overrode <c>ht-e-approve</c>'s roles to
    /// <c>xd-override-only</c>, so the roles <c>ht-e</c>'s OWN definition grants are no longer
    /// offered it — while the identical shape one level up, which nobody overrode, still offers
    /// <c>ht-d-approve</c> to exactly those roles. Without the control half, "not offered" would be
    /// satisfied just as well by a broken descent.
    /// </para>
    /// <para>
    /// The two overrides are deliberately asserted on different surfaces — <c>states</c> on the list
    /// above, <c>transitions</c> here — because that is the split this change is about: visibility is
    /// the list's question, actionability is the screen's.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task AParentsTransitionOverrideStillGovernsWhatTheLeafOffers()
    {
        Skip.If(lab.PartnerBaseUrl is null, "partner domain not configured — run labs/cross-domain/lab.sh up");
        Skip.If(credit.CreditBaseUrl is null, "credit domain not configured — run labs/cross-domain/lab.sh up");

        var overridden = await StartChainAsync(hops: 4, visibleTo: "xd-override-only");
        var htd = await ResolveLevelAsync(overridden, "ht-d");

        // The role ht-d's override names is offered the child's approve, through ht-d.
        var offeredToOverride = await AvailableTransitionsAsync(
            lab.PartnerBaseUrl!, "partner", "ht-d", htd, "xd-override-only");
        Assert.Contains("ht-e-approve", offeredToOverride);

        // The roles ht-e's OWN definition grants are not — the override replaced them, on both the
        // state's visibility and the transition's grants.
        foreach (var role in new[] { ApproverRole, "ht-e-approver" })
        {
            var offered = await AvailableTransitionsAsync(
                lab.PartnerBaseUrl!, "partner", "ht-d", htd, role);
            Assert.DoesNotContain("ht-e-approve", offered);
        }

        // A DENY inside the override wins across roles, on this surface too.
        var denied = await AvailableTransitionsAsync(
            lab.PartnerBaseUrl!, "partner", "ht-d", htd, "xd-override-only,ht-blocked");
        Assert.DoesNotContain("ht-e-approve", denied);

        // Control: the same shape one level up, which nobody overrode, still offers its approve to
        // exactly those roles. This is what separates "the override applied" from "nothing resolved".
        var untouched = await StartChainAsync(hops: 3);
        var htc = await ResolveLevelAsync(untouched, "ht-c");

        foreach (var role in new[] { ApproverRole, "ht-d-approver" })
        {
            var offered = await AvailableTransitionsAsync(CoreBaseUrl, "core", "ht-c", htc, role);
            Assert.Contains("ht-d-approve", offered);
        }
    }

    // ── Built-in functions: descent, and one answer per question ─────────────────
    //
    // A client never addresses the leaf: it holds the root. So every built-in function has to
    // descend an active subflow, and every surface has to answer the SAME question the same way at
    // both ends of that descent — otherwise the list offers work the screen refuses, or `authorize`
    // blesses a transition the state function will not show.

    /// <summary>
    /// <c>authorize</c> and the state function agree, on the leaf AND through the parent, for both
    /// questions they can be asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 4×4: {leaf, overriding parent} × {authorize, state} × the role sets the override separates.
    /// <c>authorize?transitionKey=</c> asks actionability and the state function's
    /// <c>transitions</c> answers the same thing; <c>authorize?queryRoles=true</c> asks visibility
    /// and the state function's 403 answers that one. The parameter selects the question — the two
    /// surfaces must not disagree once it is the same question.
    /// </para>
    /// <para>
    /// This diverged and was measured diverging: at a directly-addressed leaf, <c>authorize</c>
    /// resolved the parent's overrides from the PARENT's definition — which a leaf does not have —
    /// and answered from the child's own grants, giving the OPPOSITE verdict to the state function
    /// for both roles. The resolution now lives in <c>TransitionAuthorizationManager</c>, so no
    /// surface can resolve it its own way again.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task AuthorizeAndTheStateFunctionAgreeOnTheLeafAndThroughTheParent()
    {
        Skip.If(lab.PartnerBaseUrl is null, "partner domain not configured — run labs/cross-domain/lab.sh up");
        Skip.If(credit.CreditBaseUrl is null, "credit domain not configured — run labs/cross-domain/lab.sh up");

        var rootId = await StartChainAsync(hops: 4, visibleTo: "xd-override-only");
        var htd = await ResolveLevelAsync(rootId, "ht-d");
        var hte = await ResolveLevelAsync(rootId, "ht-e");

        var (partnerUrl, creditUrl) = (lab.PartnerBaseUrl!, credit.CreditBaseUrl!);

        foreach (var roles in new[] { "xd-override-only", "ht-e-approver", ApproverRole, "xd-override-only,ht-blocked" })
        {
            // Actionability, both ends of the descent.
            var authLeaf = await AuthorizeAsync(creditUrl, "credit", "ht-e", hte, roles, "?transitionKey=ht-e-approve");
            var authParent = await AuthorizeAsync(partnerUrl, "partner", "ht-d", htd, roles, "?transitionKey=ht-e-approve");
            Assert.Equal(authLeaf, authParent);

            // Visibility, both ends, and against the state function which answers the same question.
            var seesLeaf = await AuthorizeAsync(creditUrl, "credit", "ht-e", hte, roles, "?queryRoles=true");
            var seesParent = await AuthorizeAsync(partnerUrl, "partner", "ht-d", htd, roles, "?queryRoles=true");
            Assert.Equal(seesLeaf, seesParent);

            // The state function refuses with 403 when visibility is denied (null here), and
            // otherwise offers exactly what authorize blessed.
            var offeredLeaf = await OffersAsync(creditUrl, "credit", "ht-e", hte, roles, "ht-e-approve");
            Assert.Equal(seesLeaf, offeredLeaf is not null);
            if (offeredLeaf is not null)
                Assert.Equal(authLeaf, offeredLeaf);
        }
    }

    /// <summary>
    /// Only the role the parent's override names gets through — on every one of those surfaces.
    /// </summary>
    /// <remarks>
    /// The paired half of the test above: it asserts the surfaces AGREE, this asserts they agree on
    /// the right answer. Without it they could agree by all being broken the same way.
    /// </remarks>
    [SkippableFact]
    public async Task TheOverriddenRoleIsTheOnlyOneAdmittedOnEverySurface()
    {
        Skip.If(lab.PartnerBaseUrl is null, "partner domain not configured — run labs/cross-domain/lab.sh up");
        Skip.If(credit.CreditBaseUrl is null, "credit domain not configured — run labs/cross-domain/lab.sh up");

        var rootId = await StartChainAsync(hops: 4, visibleTo: "xd-override-only");
        var htd = await ResolveLevelAsync(rootId, "ht-d");
        var (partnerUrl, creditUrl) = (lab.PartnerBaseUrl!, credit.CreditBaseUrl!);
        var hte = await ResolveLevelAsync(rootId, "ht-e");

        // The override names xd-override-only and denies ht-blocked. Replace, not merge: ht-e's own
        // grants (ht-approver, ht-e-approver) no longer admit.
        Assert.True(await AuthorizeAsync(partnerUrl, "partner", "ht-d", htd, "xd-override-only", "?queryRoles=true"));
        Assert.False(await AuthorizeAsync(partnerUrl, "partner", "ht-d", htd, "ht-e-approver", "?queryRoles=true"));
        Assert.False(await AuthorizeAsync(partnerUrl, "partner", "ht-d", htd, ApproverRole, "?queryRoles=true"));

        // DENY inside the override wins across roles, on this surface too.
        Assert.False(await AuthorizeAsync(
            partnerUrl, "partner", "ht-d", htd, "xd-override-only,ht-blocked", "?queryRoles=true"));

        // And the leaf answers identically when addressed on its own url.
        Assert.True(await AuthorizeAsync(creditUrl, "credit", "ht-e", hte, "xd-override-only", "?queryRoles=true"));
        Assert.False(await AuthorizeAsync(creditUrl, "credit", "ht-e", hte, "ht-e-approver", "?queryRoles=true"));
    }

    /// <summary>
    /// The role order a caller happens to send must not change any answer.
    /// </summary>
    /// <remarks>
    /// Four surfaces used to be handed <c>ICallerRoleResolver.SingleRoleOf(roles)</c> — literally
    /// <c>roles[0]</c> — so a grant the caller held was never evaluated unless it happened to be
    /// listed first. Measured on this very chain: <c>other,ht-c-approver</c> was offered nothing
    /// while <c>ht-c-approver,other</c> was offered the transition. It also put the deny group, an
    /// AND across every role, permanently out of reach on those surfaces.
    /// </remarks>
    [SkippableFact]
    public async Task RoleOrderDoesNotChangeAnyAnswer()
    {
        var rootId = await StartChainAsync(hops: 2);
        var htc = await ResolveLevelAsync(rootId, "ht-c");
        var coreUrl = CoreBaseUrl;

        foreach (var pair in new[]
                 {
                     ("other,ht-c-approver", "ht-c-approver,other"),
                     ($"{ApproverRole},ht-blocked", $"ht-blocked,{ApproverRole}")
                 })
        {
            var (first, reversed) = pair;

            Assert.Equal(
                await OffersAsync(coreUrl, "core", "ht-c", htc, first, "ht-c-approve"),
                await OffersAsync(coreUrl, "core", "ht-c", htc, reversed, "ht-c-approve"));

            Assert.Equal(
                await AuthorizeAsync(coreUrl, "core", "ht-c", htc, first, "?transitionKey=ht-c-approve"),
                await AuthorizeAsync(coreUrl, "core", "ht-c", htc, reversed, "?transitionKey=ht-c-approve"));

            Assert.Equal(
                await AuthorizeAsync(coreUrl, "core", "ht-c", htc, first, "?queryRoles=true"),
                await AuthorizeAsync(coreUrl, "core", "ht-c", htc, reversed, "?queryRoles=true"));
        }

        // ...and the answer is the correct one, not merely a stable one: a grant the caller holds
        // counts wherever it sits in the list.
        Assert.True(await OffersAsync(coreUrl, "core", "ht-c", htc, "other,ht-c-approver", "ht-c-approve"));
    }

    /// <summary>
    /// Every built-in instance function descends an active subflow — except <c>data</c>, which is
    /// pinned here as it behaves today.
    /// </summary>
    /// <remarks>
    /// The client holds the ROOT and never the leaf, so a function that answers from the root while
    /// the state body describes the leaf hands back something about a different instance. <c>state</c>
    /// descends, and so do <c>view</c>, <c>schema</c>, <c>master</c> and <c>extensions</c>.
    /// <para>
    /// <c>data</c> does NOT, and the state body's own <c>data.href</c> addresses the root — so a
    /// client following the link it was given reads the root's attributes while looking at the leaf's
    /// state. Pinned rather than asserted-as-correct: whether a client wants the case's data or the
    /// leaf's working copy is a product decision, and this test exists so the current answer cannot
    /// change unnoticed. See TEST-SCENARIOS.md § Bilinen Kapsam Açıkları.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task TheStateFunctionDescendsButTheDataFunctionDoesNot()
    {
        var rootId = await StartChainAsync(hops: 2);
        var coreUrl = CoreBaseUrl;

        // state descends: the root reports the leaf's state.
        var (stateStatus, stateBody) = await FunctionAsync(coreUrl, "core", Root, rootId, "state", ApproverRole);
        Assert.Equal(HttpStatusCode.OK, stateStatus);
        using (var document = JsonDocument.Parse(stateBody))
        {
            Assert.Equal("ht-c-human", document.RootElement.GetProperty("state").GetString());

            // ...and the data link it hands the client addresses the ROOT, not that state's instance.
            Assert.Contains(rootId, document.RootElement.GetProperty("data").GetProperty("href").GetString()!);
        }

        // data does not descend: the root answers with its own humanTask block.
        var (dataStatus, dataBody) = await FunctionAsync(coreUrl, "core", Root, rootId, "data", ApproverRole);
        Assert.Equal(HttpStatusCode.OK, dataStatus);
        using (var document = JsonDocument.Parse(dataBody))
        {
            var title = document.RootElement.GetProperty("data").GetProperty("humanTask")
                .GetProperty("title").GetString();
            Assert.Equal("HT-A step", title);
        }
    }

    /// <summary>
    /// A SubProcess is authorized by ITS OWN leaf, like the independent flow it is.
    /// </summary>
    /// <remarks>
    /// It is listed by the domain that owns it and never by its parent's, so its grants can only
    /// come from its own descent. Were the parent's decision reused, a caller authorized on the
    /// root would inherit the SubProcess — across a domain boundary, on a case they may have no
    /// part in.
    /// </remarks>
    [SkippableFact]
    public async Task ASpawnedSubProcessIsAuthorizedByItsOwnLeaf()
    {
        Skip.If(lab.PartnerBaseUrl is null, "partner domain not configured — run labs/cross-domain/lab.sh up");
        Skip.If(credit.CreditBaseUrl is null, "credit domain not configured — run labs/cross-domain/lab.sh up");

        await StartAsyncAndGetIdAsync(new
        {
            mode = "process",
            hops = 0,
            processHops = 2,      // SubProcess descends ht-d -> ht-e -> ht-f
            testId = Guid.NewGuid().ToString("N"),
            humanTask = new { title = "HT-A step", description = "HT-A step description" }
        });

        await WaitUntilAsync(
            async () => (await ListHumanTasksAsync(lab.PartnerBaseUrl!, "partner", fresh: true))
                .Any(row => row.Workflow == "ht-d" && row.Title == "HT-F step"),
            "the spawned SubProcess never surfaced in partner's list",
            TimeSpan.FromMinutes(2));

        // ht-f-approver is granted ONLY at the SubProcess's leaf, and it sees it.
        var byLeafRole = await ListHumanTasksAsync(
            lab.PartnerBaseUrl!, "partner", roles: "ht-f-approver", fresh: true);
        Assert.Contains(byLeafRole, row => row.Workflow == "ht-d" && row.Title == "HT-F step");

        // A role granted nowhere in the chain does not.
        var byForeignRole = await ListHumanTasksAsync(
            lab.PartnerBaseUrl!, "partner", roles: "some.other.role", fresh: true);
        Assert.DoesNotContain(byForeignRole, row => row.Workflow == "ht-d");
    }
}
