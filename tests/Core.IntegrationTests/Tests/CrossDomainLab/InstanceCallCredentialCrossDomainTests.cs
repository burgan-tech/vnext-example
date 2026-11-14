using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// XC-01 … XC-08 (labs/cross-domain/VNEXT-BUILD-PLAN-instance-call-credential.md): the caller-credential rule on
/// instance calls ACROSS the domain boundary (core → partner, and partner → core for XC-06).
/// <para>
/// Rule, per credential header (<c>sub</c>, <c>act_sub</c>, <c>position</c>, <c>client_id</c>, <c>role</c>): the
/// mapping set a non-blank value → the target sees it; set a BLANK value → the credential does not reach the target
/// and the caller's value is not added; did not set the key → the target sees the caller's value (the request that
/// drove the parent's hop). Canonical statement: vnext <c>docs/domain/role-grant-authorization.md</c> § "Caller
/// credential on instance calls". The two cross-domain exits under test: the SubFlow start's header merge
/// (<c>CurrentUserForwardHeadersHelper.MergeIntoRequest</c> — a blank removes the ambient header) and the Execution
/// host's remote invokers (<c>InvokerHelpers.DropBlankCredentialHeaders</c> — the blank marker is dropped before the
/// request leaves, and the host adds no identity of its own).
/// </para>
/// <para>
/// XC-08 (parity): every expectation below is the one <c>InstanceCallCredentialLab</c> asserts for the same case in
/// one domain — same recorder, same variants, same assertions. XC-07 pins the one documented difference: a
/// cross-domain SubFlow child still receives the ambient caller's <c>userId</c>.
/// </para>
/// <para>
/// Each test starts its own <c>icc-xd-parent</c> in core (it rests in <c>icc-xd-p-ready</c>) and drives ONE manual
/// transition with its own GUID <c>sub</c> and short <c>act_sub</c>. Partner children are read by key from the
/// partner orchestrator. Every id, key and caller value is printed to the test output.
/// </para>
/// </summary>
[Collection("VNextIntegration")]
public class InstanceCallCredentialCrossDomainTests : CrossDomainLabTestBase, IClassFixture<CrossDomainLabFixture>
{
    private const string IccParent = "icc-xd-parent";
    private const string PartnerTarget = "icc-xd-target";
    private const string PartnerSub = "icc-xd-sub";
    private const string PartnerNest = "icc-xd-sub-nest";
    private const string CoreTarget = "icc-xd-core-target";
    private const string ReaderRole = "icc.reader";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    private readonly ITestOutputHelper _output;

    public InstanceCallCredentialCrossDomainTests(
        VNextTestEnvironment environment, CrossDomainLabFixture lab, ITestOutputHelper output)
        : base(environment, lab)
    {
        _output = output;
    }

    // ── XC-01 / XC-02: SubFlow start core → partner ───────────────────────────

    [SkippableFact]
    public async Task XC01_SubFlow_NoMappingHeaders_PartnerChildSeesCallerCredential_AndRunsAsCaller()
    {
        RequirePartner();
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "none" }, "do-subflow", caller);

        await WaitParentAsync(parent, "C");
        var child = await GetPartnerAsync(PartnerSub, $"{parent}-sub");
        AssertSaw(Seen(child), "sub", caller.Sub);
        AssertSaw(Seen(child), "act_sub", caller.ActSub);
        AssertCreated(child, by: caller.ActSub, behalfOf: caller.Sub);
    }

    [SkippableFact]
    public async Task XC02_SubFlow_BlankSub_PartnerChildSeesNoSub_KeepsActSub()
    {
        RequirePartner();
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "blank" }, "do-subflow", caller);

        await WaitParentAsync(parent, "C");
        var child = await GetPartnerAsync(PartnerSub, $"{parent}-sub");
        AssertDidNotSee(Seen(child), "sub");
        AssertSaw(Seen(child), "act_sub", caller.ActSub);
        AssertCreated(child, by: caller.ActSub, behalfOf: null);
    }

    // ── XC-03: SubProcess / Start core → partner ──────────────────────────────

    [SkippableTheory]
    [InlineData("do-subprocess", "proc")]
    [InlineData("do-start", "start")]
    public async Task XC03_NewInstance_NoMappingHeaders_PartnerTargetSeesCallerCredential_AndRunsAsCaller(string transition, string suffix)
    {
        RequirePartner();
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "none" }, transition, caller);

        var call = await WaitCallAsync(parent);
        var target = await GetStartedAsync(call, parent, suffix);
        AssertSaw(Seen(target), "sub", caller.Sub);
        AssertSaw(Seen(target), "act_sub", caller.ActSub);
        AssertCreated(target, by: caller.ActSub, behalfOf: caller.Sub);
    }

    [SkippableTheory]
    [InlineData("do-subprocess", "proc")]
    [InlineData("do-start", "start")]
    public async Task XC03_NewInstance_BlankSub_PartnerTargetSeesNoSub_KeepsActSub(string transition, string suffix)
    {
        RequirePartner();
        var caller = Caller.New();
        var parent = await DriveAsync(new { variant = "blank" }, transition, caller);

        var call = await WaitCallAsync(parent);
        var target = await GetStartedAsync(call, parent, suffix);
        AssertDidNotSee(Seen(target), "sub");
        AssertSaw(Seen(target), "act_sub", caller.ActSub);
        AssertCreated(target, by: caller.ActSub, behalfOf: null);
    }

    // ── XC-04: DirectTrigger core → partner ───────────────────────────────────

    [SkippableFact]
    public async Task XC04_DirectTrigger_NoMappingHeaders_PartnerTransitionSeesCallerCredential()
    {
        RequirePartner();
        var caller = Caller.New();
        var (targetId, _, _) = await StartPartnerTargetAsync(protectedField: false);
        var parent = await DriveAsync(new { variant = "none", targetId }, "do-trigger", caller);

        await WaitCallAsync(parent);
        var touch = await TouchSeenAsync(targetId);
        AssertSaw(touch, "sub", caller.Sub);
        AssertSaw(touch, "act_sub", caller.ActSub);
    }

    [SkippableFact]
    public async Task XC04_DirectTrigger_BlankSub_PartnerTransitionSeesNoSub_KeepsActSub()
    {
        RequirePartner();
        var caller = Caller.New();
        var (targetId, _, _) = await StartPartnerTargetAsync(protectedField: false);
        var parent = await DriveAsync(new { variant = "blank", targetId }, "do-trigger", caller);

        await WaitCallAsync(parent);
        var touch = await TouchSeenAsync(targetId);
        AssertDidNotSee(touch, "sub");
        AssertSaw(touch, "act_sub", caller.ActSub);
    }

    // ── XC-05: GetInstance / GetInstances / GetInstanceData core → partner ────

    [SkippableTheory]
    [InlineData("do-get")]
    [InlineData("do-list")]
    [InlineData("do-data")]
    public async Task XC05_Read_NoMappingHeaders_CallerRoleCrosses_ProtectedFieldReturned(string transition)
    {
        RequirePartner();
        var caller = Caller.New(ReaderRole);
        var (targetId, testId, secret) = await StartPartnerTargetAsync(protectedField: true);
        var parent = await DriveAsync(new { variant = "none", targetId, testId }, transition, caller);

        var call = await WaitCallAsync(parent);
        Assert.True(call.GetProperty("isSuccess").GetBoolean(), $"read failed: {call.GetRawText()}");
        Assert.True(call.GetProperty("hasSecret").GetBoolean(), $"the read did not return secretNote: {call.GetRawText()}");
        Assert.Equal(secret, call.GetProperty("secretNote").GetString());
    }

    [SkippableTheory]
    [InlineData("do-get")]
    [InlineData("do-list")]
    [InlineData("do-data")]
    public async Task XC05_Read_BlankRoleHeader_CallerRoleWithheld_ProtectedFieldNotReturned(string transition)
    {
        RequirePartner();
        var caller = Caller.New(ReaderRole);
        var (targetId, testId, _) = await StartPartnerTargetAsync(protectedField: true);
        var parent = await DriveAsync(new { variant = "role-blank", targetId, testId }, transition, caller);

        var call = await WaitCallAsync(parent);
        Assert.True(call.GetProperty("isSuccess").GetBoolean(), $"read failed: {call.GetRawText()}");
        Assert.False(call.GetProperty("hasSecret").GetBoolean(), $"the read returned secretNote without the role: {call.GetRawText()}");
    }

    // ── XC-06: nested — partner child calls back into core ────────────────────

    /// <summary>
    /// The core parent starts the partner SubFlow <c>icc-xd-sub-nest</c> with <c>sub = ""</c>. That child, with no
    /// mapping headers of its own, runs DirectTrigger and GetInstance back into core (<c>useDapr</c>) on a core target
    /// the test started with the caller's own <c>sub</c>. Neither call may carry <c>sub</c>: the trigger's
    /// <c>touchSeen</c> has none, and the read does not return <c>starterNote</c> (x-roles allow
    /// <c>$InstanceBehalfOfStarter</c> — visible only to the target's behalf-of starter, i.e. that <c>sub</c>).
    /// </summary>
    [SkippableFact]
    public async Task XC06_Nested_BlankSubOnPartnerSubFlow_NotRefilledOnTheWayBackIntoCore()
    {
        RequirePartner();
        var caller = Caller.New();
        var coreTargetId = await StartCoreTargetAsync(caller);
        var parent = await DriveAsync(new { variant = "blank", coreTargetId }, "do-nest", caller);

        var data = await WaitParentAsync(parent, "C");
        var child = await GetPartnerAsync(PartnerNest, $"{parent}-sub");
        AssertDidNotSee(Seen(child), "sub");
        AssertSaw(Seen(child), "act_sub", caller.ActSub);
        AssertBackCallsSucceeded(data);

        var target = await GetCoreAsync(CoreTarget, coreTargetId);
        Assert.Equal("icc-t-touched", target.GetProperty("metadata").GetProperty("currentState").GetString());
        var touch = target.GetProperty("attributes").GetProperty("touchSeen");
        AssertDidNotSee(touch, "sub");
        AssertSaw(touch, "act_sub", caller.ActSub);

        var read = data.GetProperty("backRead");
        Assert.False(read.GetProperty("hasStarterNote").GetBoolean(),
            $"the read back into core carried the caller's sub (starterNote returned): {read.GetRawText()}");
    }

    /// <summary>
    /// The control for XC-06: the same nested path with NO mapping headers carries the caller's <c>sub</c> all the
    /// way back into core — the trigger sees it and the read returns <c>starterNote</c>. Without this, a green XC-06
    /// could mean the back-calls never carried anything.
    /// </summary>
    [SkippableFact]
    public async Task XC06_Control_NoMappingHeaders_CallerSubReachesCoreThroughThePartnerChild()
    {
        RequirePartner();
        var caller = Caller.New();
        var coreTargetId = await StartCoreTargetAsync(caller);
        var parent = await DriveAsync(new { variant = "none", coreTargetId }, "do-nest", caller);

        var data = await WaitParentAsync(parent, "C");
        AssertBackCallsSucceeded(data);

        var target = await GetCoreAsync(CoreTarget, coreTargetId);
        var touch = target.GetProperty("attributes").GetProperty("touchSeen");
        AssertSaw(touch, "sub", caller.Sub);
        AssertSaw(touch, "act_sub", caller.ActSub);

        var read = data.GetProperty("backRead");
        Assert.True(read.GetProperty("hasStarterNote").GetBoolean(),
            $"the read back into core did not return starterNote with the caller's sub: {read.GetRawText()}");
    }

    // ── XC-07: the kept difference ────────────────────────────────────────────

    /// <summary>
    /// The caller sends <c>userId</c>; a cross-domain SubFlow child still receives it from the ambient caller (a
    /// same-domain child does not). Kept on purpose (product-owner decision 2026-11-13); this test pins it.
    /// </summary>
    [SkippableFact]
    public async Task XC07_SubFlow_CrossDomainChildStillReceivesAmbientUserId()
    {
        RequirePartner();
        var caller = Caller.New(userId: $"icc-user-{Guid.NewGuid():N}"[..17]);
        var parent = await DriveAsync(new { variant = "none" }, "do-subflow", caller);

        await WaitParentAsync(parent, "C");
        var child = await GetPartnerAsync(PartnerSub, $"{parent}-sub");
        AssertSaw(Seen(child), "userid", caller.UserId!);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Caller(string Sub, string ActSub, string? Role, string? UserId)
    {
        // `sub` lands in a varchar(36) identity column: a GUID fits, anything longer fails the save with 500.
        public static Caller New(string? role = null, string? userId = null) =>
            new(Guid.NewGuid().ToString(), $"icc-{Guid.NewGuid():N}"[..12], role, userId);

        public Dictionary<string, string> ToHeaders()
        {
            var headers = Headers(Role);
            headers["sub"] = Sub;
            headers["act_sub"] = ActSub;
            if (UserId is not null) headers["userId"] = UserId;
            return headers;
        }
    }

    /// <summary>Starts icc-xd-parent in core with <paramref name="data"/> and drives ONE call transition with the caller's headers.</summary>
    private async Task<string> DriveAsync(object data, string transition, Caller caller)
    {
        var parentId = await StartAsync(IccParent, data);
        Assert.Equal(("icc-xd-p-ready", "A"), await GetInstanceStateAsync(IccParent, parentId));
        _output.WriteLine($"core parent={parentId} transition={transition} data={JsonSerializer.Serialize(data)} " +
                          $"caller sub={caller.Sub} act_sub={caller.ActSub} role={caller.Role ?? "-"} userId={caller.UserId ?? "-"}");

        var url = $"api/v1/{TestDomain.Name}/workflows/{IccParent}/instances/{parentId}/transitions/{transition}?sync=false";
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

    /// <summary>
    /// Starts an icc-xd-target in PARTNER that parks in icc-t-wait (the DirectTrigger / read target). With
    /// <paramref name="protectedField"/> it carries <c>secretNote</c> and the control is checked: a partner GET with
    /// <c>icc.reader</c> sees it, one without the role does not.
    /// </summary>
    private async Task<(string TargetId, string TestId, string Secret)> StartPartnerTargetAsync(bool protectedField)
    {
        var testId = $"icc-xd-{Guid.NewGuid():N}";
        var secret = $"secret-{Guid.NewGuid():N}"[..20];
        object body = protectedField ? new { mode = "wait", testId, secretNote = secret } : new { mode = "wait", testId };
        var response = await PartnerApi.StartInstanceAsync(PartnerTarget, body, Headers());
        Assert.True(response.IsSuccessStatusCode, $"partner start of {PartnerTarget} answered {(int)response.StatusCode}: {response.RawBody}");
        var targetId = response.Body.GetProperty("id").GetString()!;
        _output.WriteLine($"partner target={targetId} testId={testId}{(protectedField ? $" secretNote={secret}" : "")}");
        await WaitForPartnerStateAsync(PartnerTarget, targetId, "icc-t-wait");

        if (protectedField)
        {
            var withRole = (await GetPartnerAsync(PartnerTarget, targetId, ReaderRole)).GetProperty("attributes");
            Assert.True(withRole.TryGetProperty("secretNote", out _), $"control: icc.reader partner GET lacks secretNote: {withRole.GetRawText()}");
            var withoutRole = (await GetPartnerAsync(PartnerTarget, targetId)).GetProperty("attributes");
            Assert.False(withoutRole.TryGetProperty("secretNote", out _), $"control: role-less partner GET sees secretNote: {withoutRole.GetRawText()}");
        }

        return (targetId, testId, secret);
    }

    /// <summary>
    /// Starts the XC-06 core target AS the caller (its <c>sub</c> becomes the row's <c>CreatedByBehalfOf</c>), parked
    /// in icc-t-wait, with <c>starterNote</c>; control: a core GET with that <c>sub</c> sees the note, one without
    /// does not.
    /// </summary>
    private async Task<string> StartCoreTargetAsync(Caller caller)
    {
        var note = $"note-{Guid.NewGuid():N}"[..16];
        var response = await Api.StartInstanceAsync(CoreTarget, new { mode = "wait", starterNote = note }, caller.ToHeaders());
        Assert.True(response.IsSuccessStatusCode, $"core start of {CoreTarget} answered {(int)response.StatusCode}: {response.RawBody}");
        var targetId = response.Body.GetProperty("id").GetString()!;
        _output.WriteLine($"core target={targetId} starterNote={note}");
        Assert.Equal("icc-t-wait", (await GetInstanceStateAsync(CoreTarget, targetId)).State);

        var asStarter = (await GetCoreAsync(CoreTarget, targetId, caller.ToHeaders())).GetProperty("attributes");
        Assert.True(asStarter.TryGetProperty("starterNote", out _), $"control: the behalf-of starter's GET lacks starterNote: {asStarter.GetRawText()}");
        var asOther = (await GetCoreAsync(CoreTarget, targetId)).GetProperty("attributes");
        Assert.False(asOther.TryGetProperty("starterNote", out _), $"control: a sub-less GET sees starterNote: {asOther.GetRawText()}");
        return targetId;
    }

    private async Task<JsonElement> WaitParentAsync(string parentId, string status)
    {
        await WaitUntilAsync(async () =>
            {
                var current = (await GetInstanceStateAsync(IccParent, parentId)).Status;
                if (current != status && TerminalStatuses.Contains(current))
                    Assert.Fail($"{IccParent} settled {current}, expected {status} — {await DescribeAsync(IccParent, parentId)}");
                return current == status;
            },
            $"{IccParent} never reached status {status} — {await DescribeAsync(IccParent, parentId)}", Budget);
        var data = await GetAttributesAsync(IccParent, parentId);
        _output.WriteLine($"core parent={parentId} status={status} data={data.GetRawText()}");
        return data;
    }

    private async Task<JsonElement> WaitCallAsync(string parentId)
    {
        var data = await WaitParentAsync(parentId, "C");
        Assert.True(data.TryGetProperty("call", out var call), $"the parent recorded no call: {data.GetRawText()}");
        return call;
    }

    /// <summary>
    /// The partner instance a SubProcess / Start call created, read BY KEY: the mapping sets <c>{parentId}-proc</c> /
    /// <c>{parentId}-start</c>. Also pins that the key arrived in partner — the cross-domain Start dropped it until
    /// vnext bc127eda (<c>TaskBindingMapper.MapStartTask</c> did not copy <c>Key</c> onto the remote binding).
    /// </summary>
    private async Task<JsonElement> GetStartedAsync(JsonElement call, string parentId, string suffix)
    {
        Assert.True(call.GetProperty("isSuccess").GetBoolean(), $"the {suffix} call failed: {call.GetRawText()}");
        var key = $"{parentId}-{suffix}";
        var target = await GetPartnerAsync(PartnerTarget, key);
        Assert.Equal(key, Text(target, "key"));
        return target;
    }

    private static void AssertBackCallsSucceeded(JsonElement parentData)
    {
        foreach (var name in new[] { "backTrigger", "backRead" })
        {
            Assert.True(parentData.TryGetProperty(name, out var call) && call.ValueKind == JsonValueKind.Object,
                $"the nested child recorded no {name}: {parentData.GetRawText()}");
            Assert.True(call.TryGetProperty("isSuccess", out var ok) && ok.ValueKind == JsonValueKind.True,
                $"the nested child's {name} back into core failed: {call.GetRawText()}");
        }
    }

    private async Task<JsonElement> TouchSeenAsync(string targetId)
    {
        var target = await GetPartnerAsync(PartnerTarget, targetId);
        Assert.Equal("icc-t-touched", target.GetProperty("metadata").GetProperty("currentState").GetString());
        Assert.True(target.GetProperty("attributes").TryGetProperty("touchSeen", out var touch),
            $"the partner target recorded no touch: {target.GetRawText()}");
        return touch;
    }

    private async Task<JsonElement> GetPartnerAsync(string workflow, string identifier, string? roles = null)
    {
        var response = await PartnerApi.GetInstanceAsync(workflow, identifier, Headers(roles));
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"partner GET {workflow}/{identifier} answered {(int)response.StatusCode}: {response.RawBody}");
        var element = response.Body.Clone();
        Log("partner", workflow, identifier, element);
        return element;
    }

    private async Task<JsonElement> GetCoreAsync(string workflow, string identifier, Dictionary<string, string>? headers = null)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{identifier}", headers: headers ?? Headers());
        Assert.True(status == HttpStatusCode.OK, $"core GET {workflow}/{identifier} answered {(int)status}: {body}");
        var element = JsonDocument.Parse(body).RootElement.Clone();
        Log("core", workflow, identifier, element);
        return element;
    }

    private void Log(string domain, string workflow, string identifier, JsonElement element)
    {
        var metadata = element.GetProperty("metadata");
        _output.WriteLine($"{domain} {workflow}/{identifier}: id={Text(element, "id")} key={Text(element, "key")} createdBy={Text(metadata, "createdBy")} " +
                          $"createdByBehalfOf={Text(metadata, "createdByBehalfOf")} attributes={element.GetProperty("attributes").GetRawText()}");
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
        Assert.False(seen.GetProperty("has_" + header).GetBoolean(),
            $"'{header}' reached the target (value '{value}'): {seen.GetRawText()}");
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
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
