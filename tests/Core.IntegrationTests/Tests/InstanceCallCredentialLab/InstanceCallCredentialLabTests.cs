using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Core.IntegrationTests.Tests.InstanceCallCredentialLab;

/// <summary>
/// instance-call-credential-lab: the caller-credential rule on instance calls, same domain (<c>core</c>).
/// <para>
/// When the runtime calls another instance — a SubFlow start or a SubProcess / Start / DirectTrigger /
/// GetInstance / GetInstances / GetInstanceData task — it presents <c>sub</c>, <c>act_sub</c>,
/// <c>position</c>, <c>client_id</c> and <c>role</c> by one rule, per header: the mapping set a non-blank
/// value → the target sees it; the mapping set a BLANK value → the credential does not reach the target
/// and the caller's value is not added; the mapping did not set the key → the target sees the caller's
/// value (the header of the request that drove the parent's hop). An in-process call also runs AS the
/// resolved user: a started child's row has <c>createdBy</c> = <c>act_sub</c>,
/// <c>createdByBehalfOf</c> = <c>sub</c>. An unreadable SubFlow <c>Headers</c> shape fails the start before
/// a child exists. Canonical statement: vnext <c>docs/domain/role-grant-authorization.md</c> § "Caller
/// credential on instance calls".
/// </para>
/// <para>
/// Every test starts its own icc-parent (it rests in icc-p-ready), then drives exactly ONE manual
/// transition with its own GUID <c>sub</c> and short <c>act_sub</c> (and <c>role</c> for the reads). The
/// target records what it saw in <c>context.Headers</c> (IccRecordMapping) and the test reads that record
/// and the target's row metadata. Each test prints its ids and caller values to the test output.
/// </para>
/// </summary>
public class InstanceCallCredentialLabTests : WorkflowTestBase
{
    private const string Parent = "icc-parent";
    private const string Target = "icc-target";
    private const string SubChild = "icc-sub";
    private const string NestChild = "icc-sub-nest";
    private const string ReaderRole = "icc.reader";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public InstanceCallCredentialLabTests(VNextTestEnvironment environment, ITestOutputHelper output)
        : base(environment)
    {
        _output = output;
    }

    // ── SubFlow start (S child) ───────────────────────────────────────────────

    [Fact]
    public async Task SubFlow_NoMappingHeaders_ChildSeesCallerCredential_AndRunsAsCaller()
    {
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "none" }, "do-subflow", caller);

        var data = await WaitParentAsync(parent, "C");
        var child = await GetInstanceAsync(SubChild, $"{parent}-sub");
        AssertSaw(Seen(child), "sub", caller.Sub);
        AssertSaw(Seen(child), "act_sub", caller.ActSub);
        AssertSaw(data.GetProperty("childSeen"), "sub", caller.Sub);
        AssertCreated(child, by: caller.ActSub, behalfOf: caller.Sub);
    }

    [Fact]
    public async Task SubFlow_DictionaryBlankSub_ChildSeesNoSub_KeepsActSub()
    {
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "dict-blank" }, "do-subflow", caller);

        await WaitParentAsync(parent, "C");
        var child = await GetInstanceAsync(SubChild, $"{parent}-sub");
        AssertDidNotSee(Seen(child), "sub");
        AssertSaw(Seen(child), "act_sub", caller.ActSub);
        AssertCreated(child, by: caller.ActSub, behalfOf: null);
    }

    /// <summary>
    /// <c>Headers = new { sub = "" }</c> (anonymous object) — a documented readable shape. The child is
    /// asserted first (the credential rule), the parent's completion last: the runtime compiles the SubFlow
    /// OUTPUT mapping as <c>CompileToInstanceAsync&lt;object&gt;</c> and instantiates the script's anonymous
    /// type instead of the mapping class, so the parent faults (<c>Instance:100030</c>). That last assert is red
    /// until the runtime picks the mapping class — see the README, "Runtime findings".
    /// </summary>
    [Fact]
    public async Task SubFlow_AnonymousObjectBlankSub_ChildSeesNoSub_KeepsActSub()
    {
        var caller = Caller.New();
        var parent = await DriveAsync(new { }, "do-subflow-anon", caller);

        await WaitUntilAsync(async () => TerminalStatuses.Contains((await GetInstanceStateAsync(Parent, parent)).Status),
            $"{Parent} never settled — {await DescribeAsync(Parent, parent)}", Budget);
        var child = await GetInstanceAsync(SubChild, $"{parent}-anon");
        AssertDidNotSee(Seen(child), "sub");
        AssertSaw(Seen(child), "act_sub", caller.ActSub);
        AssertCreated(child, by: caller.ActSub, behalfOf: null);

        var (state, status) = await GetInstanceStateAsync(Parent, parent);
        Assert.True(status == "C",
            $"child is right, but the parent settled {state}/{status} — {await DescribeAsync(Parent, parent)}");
    }

    [Fact]
    public async Task SubFlow_MappingSetsAnotherSub_ChildSeesMappingValue()
    {
        var caller = Caller.New();
        var otherSub = Guid.NewGuid().ToString();
        _output.WriteLine($"otherSub={otherSub}");
        var parent = await DriveAsync(new { variant = "other", otherSub }, "do-subflow", caller);

        await WaitParentAsync(parent, "C");
        var child = await GetInstanceAsync(SubChild, $"{parent}-sub");
        AssertSaw(Seen(child), "sub", otherSub);
        AssertSaw(Seen(child), "act_sub", caller.ActSub);
        AssertCreated(child, by: caller.ActSub, behalfOf: otherSub);
    }

    /// <summary>
    /// <c>Headers = "sub="</c> is not a header set: the start fails (<c>SubFlowInputMappingFailed</c>)
    /// before a child exists. Established expectation for a failed SubFlow start in this runtime
    /// (SubflowStartFailureLab): the parent faults in the SubFlow state with an active incident.
    /// </summary>
    [Fact]
    public async Task SubFlow_StringHeaders_FailsTheStart_NoChildExists()
    {
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "string" }, "do-subflow", caller);

        await WaitUntilAsync(async () => TerminalStatuses.Contains((await GetInstanceStateAsync(Parent, parent)).Status),
            $"{Parent} never settled — {await DescribeAsync(Parent, parent)}", Budget);
        var (state, status) = await GetInstanceStateAsync(Parent, parent);
        _output.WriteLine($"parent={parent} {state}/{status} — {await DescribeAsync(Parent, parent)}");
        Assert.Equal("F", status);
        Assert.Equal("icc-p-subflow", state);

        var (incidentStatus, incidentBody) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{Parent}/instances/{parent}/incidents/active", headers: Headers());
        _output.WriteLine($"incident {(int)incidentStatus}: {incidentBody}");
        Assert.Equal(HttpStatusCode.OK, incidentStatus);
        Assert.Contains("Headers", incidentBody, StringComparison.Ordinal);

        var (childStatus, childBody) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{SubChild}/instances/{parent}-sub", headers: Headers());
        Assert.True(childStatus == HttpStatusCode.NotFound, $"a child exists under {parent}-sub ({(int)childStatus}): {childBody}");
        foreach (var (_, childId) in await GetActiveSubflowsAsync(Parent, parent))
        {
            var (byIdStatus, byIdBody) = await SendRawAsync(HttpMethod.Get,
                $"api/v1/{TestDomain.Name}/workflows/{SubChild}/instances/{childId}", headers: Headers());
            Assert.True(byIdStatus == HttpStatusCode.NotFound, $"correlated child {childId} exists ({(int)byIdStatus}): {byIdBody}");
        }
    }

    // ── SubProcess / Start task (new instance) ────────────────────────────────

    [Fact]
    public async Task SubProcess_NoMappingHeaders_ChildSeesCallerCredential_AndRunsAsCaller()
    {
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "none" }, "do-subprocess", caller);

        await WaitCallAsync(parent);
        var child = await GetInstanceAsync(Target, $"{parent}-proc");
        AssertSaw(Seen(child), "sub", caller.Sub);
        AssertSaw(Seen(child), "act_sub", caller.ActSub);
        AssertCreated(child, by: caller.ActSub, behalfOf: caller.Sub);
    }

    [Fact]
    public async Task SubProcess_BlankSub_ChildSeesNoSub_KeepsActSub()
    {
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "blank" }, "do-subprocess", caller);

        await WaitCallAsync(parent);
        var child = await GetInstanceAsync(Target, $"{parent}-proc");
        AssertDidNotSee(Seen(child), "sub");
        AssertSaw(Seen(child), "act_sub", caller.ActSub);
        AssertCreated(child, by: caller.ActSub, behalfOf: null);
    }

    [Fact]
    public async Task Start_NoMappingHeaders_TargetSeesCallerCredential_AndRunsAsCaller()
    {
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "none" }, "do-start", caller);

        await WaitCallAsync(parent);
        var started = await GetInstanceAsync(Target, $"{parent}-start");
        AssertSaw(Seen(started), "sub", caller.Sub);
        AssertSaw(Seen(started), "act_sub", caller.ActSub);
        AssertCreated(started, by: caller.ActSub, behalfOf: caller.Sub);
    }

    [Fact]
    public async Task Start_BlankSub_TargetSeesNoSub_KeepsActSub()
    {
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "blank" }, "do-start", caller);

        await WaitCallAsync(parent);
        var started = await GetInstanceAsync(Target, $"{parent}-start");
        AssertDidNotSee(Seen(started), "sub");
        AssertSaw(Seen(started), "act_sub", caller.ActSub);
        AssertCreated(started, by: caller.ActSub, behalfOf: null);
    }

    // ── DirectTrigger task (existing instance) ────────────────────────────────

    [Fact]
    public async Task DirectTrigger_NoMappingHeaders_TargetTransitionSeesCallerCredential()
    {
        var caller = Caller.New();
        var targetId = await StartWaitingTargetAsync();
        var parent = await DriveAsync(new { variant = "none", targetId }, "do-trigger", caller);

        await WaitCallAsync(parent);
        var target = await GetInstanceAsync(Target, targetId);
        Assert.Equal("icc-t-touched", target.GetProperty("metadata").GetProperty("currentState").GetString());
        var touch = target.GetProperty("attributes").GetProperty("touchSeen");
        AssertSaw(touch, "sub", caller.Sub);
        AssertSaw(touch, "act_sub", caller.ActSub);
    }

    [Fact]
    public async Task DirectTrigger_BlankSub_TargetTransitionSeesNoSub_KeepsActSub()
    {
        var caller = Caller.New();
        var targetId = await StartWaitingTargetAsync();
        var parent = await DriveAsync(new { variant = "blank", targetId }, "do-trigger", caller);

        await WaitCallAsync(parent);
        var target = await GetInstanceAsync(Target, targetId);
        Assert.Equal("icc-t-touched", target.GetProperty("metadata").GetProperty("currentState").GetString());
        var touch = target.GetProperty("attributes").GetProperty("touchSeen");
        AssertDidNotSee(touch, "sub");
        AssertSaw(touch, "act_sub", caller.ActSub);
    }

    // ── GetInstance / GetInstances / GetInstanceData (role-protected field) ───

    [Theory]
    [InlineData("do-get")]
    [InlineData("do-list")]
    [InlineData("do-data")]
    public async Task Read_NoMappingHeaders_CallerRoleReachesTheRead_ProtectedFieldReturned(string transition)
    {
        var caller = Caller.New(ReaderRole);
        var (targetId, testId, secret) = await StartProtectedTargetAsync();
        var parent = await DriveAsync(new { variant = "none", targetId, testId }, transition, caller);

        var call = await WaitCallAsync(parent);
        Assert.True(call.GetProperty("isSuccess").GetBoolean(), $"read failed: {call.GetRawText()}");
        Assert.True(call.GetProperty("hasSecret").GetBoolean(), $"the read did not return secretNote: {call.GetRawText()}");
        Assert.Equal(secret, call.GetProperty("secretNote").GetString());
    }

    [Theory]
    [InlineData("do-get")]
    [InlineData("do-list")]
    [InlineData("do-data")]
    public async Task Read_BlankRoleHeader_CallerRoleWithheld_ProtectedFieldNotReturned(string transition)
    {
        var caller = Caller.New(ReaderRole);
        var (targetId, testId, _) = await StartProtectedTargetAsync();
        var parent = await DriveAsync(new { variant = "role-blank", targetId, testId }, transition, caller);

        var call = await WaitCallAsync(parent);
        Assert.True(call.GetProperty("isSuccess").GetBoolean(), $"read failed: {call.GetRawText()}");
        Assert.False(call.GetProperty("hasSecret").GetBoolean(), $"the read returned secretNote without the role: {call.GetRawText()}");
    }

    // ── nested ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The parent starts icc-sub-nest with <c>sub = ""</c>; that child (no mapping headers of its own) starts
    /// an icc-target. The grandchild must not see the original caller's <c>sub</c>: the caller of the
    /// child's hop is the child's own resolved header set, never the first caller (no baggage refill).
    /// </summary>
    [Fact]
    public async Task Nested_BlankSubOnSubFlow_NotRefilledOneLevelDown()
    {
        var caller = Caller.New();
        var parent = await DriveAsync(new { }, "do-nest", caller);

        var data = await WaitParentAsync(parent, "C");
        var child = await GetInstanceAsync(NestChild, $"{parent}-nest");
        AssertDidNotSee(Seen(child), "sub");
        AssertSaw(Seen(child), "act_sub", caller.ActSub);

        var nestKey = data.GetProperty("nestKey").GetString()!;
        var grandchild = await GetInstanceAsync(Target, nestKey);
        AssertDidNotSee(Seen(grandchild), "sub");
        AssertSaw(Seen(grandchild), "act_sub", caller.ActSub);
        AssertCreated(grandchild, by: caller.ActSub, behalfOf: null);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Caller(string Sub, string ActSub, string? Role)
    {
        // `sub` lands in a varchar(36) identity column: a GUID fits, anything longer fails the save with 500.
        public static Caller New(string? role = null) =>
            new(Guid.NewGuid().ToString(), $"icc-{Guid.NewGuid():N}"[..12], role);

        public Dictionary<string, string> ToHeaders()
        {
            var headers = Headers(Role);
            headers["sub"] = Sub;
            headers["act_sub"] = ActSub;
            return headers;
        }
    }

    /// <summary>Starts icc-parent with <paramref name="data"/> and drives ONE call transition with the caller's headers.</summary>
    private async Task<string> DriveAsync(object data, string transition, Caller caller)
    {
        var parentId = await StartAsync(Parent, data);
        Assert.Equal(("icc-p-ready", "A"), await GetInstanceStateAsync(Parent, parentId));
        _output.WriteLine($"parent={parentId} transition={transition} data={JsonSerializer.Serialize(data)} " +
                          $"caller sub={caller.Sub} act_sub={caller.ActSub} role={caller.Role ?? "-"}");

        var url = $"api/v1/{TestDomain.Name}/workflows/{Parent}/instances/{parentId}/transitions/{transition}?sync=false";
        HttpStatusCode status = default;
        var body = "";
        for (var attempt = 1; attempt <= LockContentionAttempts; attempt++)
        {
            (status, body) = await SendRawAsync(HttpMethod.Patch, url, new { }, caller.ToHeaders());
            if (status != HttpStatusCode.Conflict || !body.Contains("Failed to acquire lock", StringComparison.Ordinal)) break;
            await Task.Delay(LockContentionDelay);
        }

        Assert.True((int)status < 400, $"'{transition}' on {parentId} was refused with {(int)status}: {body}");
        return parentId;
    }

    /// <summary>Starts an icc-target that parks in icc-t-wait (the DirectTrigger target).</summary>
    private async Task<string> StartWaitingTargetAsync()
    {
        var targetId = await StartAsync(Target, new { mode = "wait" });
        Assert.Equal("icc-t-wait", (await GetInstanceStateAsync(Target, targetId)).State);
        _output.WriteLine($"target={targetId}");
        return targetId;
    }

    /// <summary>
    /// Starts a parked icc-target carrying the role-protected <c>secretNote</c>, and checks the control:
    /// a direct GET with <c>icc.reader</c> sees it, one without the role does not.
    /// </summary>
    private async Task<(string TargetId, string TestId, string Secret)> StartProtectedTargetAsync()
    {
        var testId = $"icc-{Guid.NewGuid():N}";
        var secret = $"secret-{Guid.NewGuid():N}"[..20];
        var targetId = await StartAsync(Target, new { mode = "wait", testId, secretNote = secret });
        _output.WriteLine($"target={targetId} testId={testId} secretNote={secret}");

        var withRole = await GetAttributesAsync(Target, targetId, ReaderRole);
        Assert.True(withRole.TryGetProperty("secretNote", out _), $"control: icc.reader GET lacks secretNote: {withRole.GetRawText()}");
        var withoutRole = await GetAttributesAsync(Target, targetId);
        Assert.False(withoutRole.TryGetProperty("secretNote", out _), $"control: role-less GET sees secretNote: {withoutRole.GetRawText()}");
        return (targetId, testId, secret);
    }

    private async Task<JsonElement> WaitParentAsync(string parentId, string status)
    {
        await WaitUntilAsync(async () =>
            {
                var current = (await GetInstanceStateAsync(Parent, parentId)).Status;
                if (current != status && TerminalStatuses.Contains(current))
                    Assert.Fail($"{Parent} settled {current}, expected {status} — {await DescribeAsync(Parent, parentId)}");
                return current == status;
            },
            $"{Parent} never reached status {status} — {await DescribeAsync(Parent, parentId)}", Budget);
        var data = await GetAttributesAsync(Parent, parentId);
        _output.WriteLine($"parent={parentId} status={status} data={data.GetRawText()}");
        return data;
    }

    /// <summary>Waits for the parent to finish its call state and returns what the call recorded.</summary>
    private async Task<JsonElement> WaitCallAsync(string parentId)
    {
        var data = await WaitParentAsync(parentId, "C");
        Assert.True(data.TryGetProperty("call", out var call), $"the parent recorded no call: {data.GetRawText()}");
        return call;
    }

    private async Task<JsonElement> GetInstanceAsync(string workflow, string identifier)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{identifier}", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"GET {workflow}/{identifier} answered {(int)status}: {body}");
        var element = JsonDocument.Parse(body).RootElement.Clone();
        var metadata = element.GetProperty("metadata");
        _output.WriteLine($"{workflow}/{identifier}: createdBy={Text(metadata, "createdBy")} " +
                          $"createdByBehalfOf={Text(metadata, "createdByBehalfOf")} attributes={element.GetProperty("attributes").GetRawText()}");
        return element;
    }

    private static JsonElement Seen(JsonElement instance)
    {
        Assert.True(instance.GetProperty("attributes").TryGetProperty("seen", out var seen),
            $"the target recorded nothing: {instance.GetRawText()}");
        return seen;
    }

    private static void AssertSaw(JsonElement seen, string header, string expected)
    {
        Assert.True(seen.GetProperty("has_" + header).GetBoolean(), $"'{header}' did not reach the target: {seen.GetRawText()}");
        Assert.Equal(expected, seen.GetProperty(header).GetString());
    }

    private static void AssertDidNotSee(JsonElement seen, string header)
    {
        var value = seen.TryGetProperty(header, out var raw) && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
        Assert.False(seen.GetProperty("has_" + header).GetBoolean() && !string.IsNullOrEmpty(value),
            $"'{header}' reached the target as '{value}': {seen.GetRawText()}");
        Assert.False(seen.GetProperty("has_" + header).GetBoolean(),
            $"'{header}' reached the target (blank value '{value}'): {seen.GetRawText()}");
    }

    private static void AssertCreated(JsonElement instance, string by, string? behalfOf)
    {
        var metadata = instance.GetProperty("metadata");
        Assert.Equal(by, Text(metadata, "createdBy"));
        if (behalfOf is null)
            Assert.True(string.IsNullOrEmpty(Text(metadata, "createdByBehalfOf")),
                $"createdByBehalfOf should be empty, was '{Text(metadata, "createdByBehalfOf")}'");
        else
            Assert.Equal(behalfOf, Text(metadata, "createdByBehalfOf"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
