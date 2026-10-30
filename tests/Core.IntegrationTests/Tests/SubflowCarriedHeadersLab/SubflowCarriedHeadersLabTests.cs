using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Core.IntegrationTests.Tests.SubflowCarriedHeadersLab;

/// <summary>
/// subflow-carried-headers-lab: the headers of the request that settled a SubFlow child ride its
/// terminal event to the parent. The parent's output mapping sees them as <c>context.Headers</c>
/// (Workflow:SubFlow:CarriedHeaders:ExposeToOutputMapping, default on); the parent's resume context
/// sees them while ApplyToResume=true (the default; a domain opts out with false). Each test uses its own
/// <c>sub</c> and its own <c>authorization</c> canary, both printed to the test output.
/// </summary>
/// <remarks>
/// Mode switch: the runtime's ApplyToResume cannot be read from outside, so the caller declares an
/// opted-out runtime with <c>VNEXT_IT_CARRIED_HEADERS_APPLY_TO_RESUME=0</c> (runtime started with
/// <c>Workflow__SubFlow__CarriedHeaders__ApplyToResume=false</c>). Unset ⇒ default (on) tests run and the
/// off-mode test skips; "0" ⇒ the reverse.
/// </remarks>
public class SubflowCarriedHeadersLabTests : WorkflowTestBase
{
    private const string Parent = "sfch-parent";
    private const string Child = "sfch-child";
    private const string ModeVariable = "VNEXT_IT_CARRIED_HEADERS_APPLY_TO_RESUME";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public SubflowCarriedHeadersLabTests(VNextTestEnvironment environment, ITestOutputHelper output)
        : base(environment)
    {
        _output = output;
    }

    private static bool ApplyToResumeOn => System.Environment.GetEnvironmentVariable(ModeVariable) != "0";

    [Fact]
    public async Task FinisherHeaders_ReachParentOutputMapping()
    {
        var run = await RunManualAsync("finish");

        var data = await WaitParentAsync(run.ParentId, "C");
        var carried = data.GetProperty("carried");
        Assert.Equal(run.Sub, carried.GetProperty("sub").GetString());
        Assert.Equal("v1", carried.GetProperty("custom").GetString());
        Assert.False(carried.GetProperty("hasAuthorization").GetBoolean(), "authorization crossed the deny floor");
        Assert.False(carried.GetProperty("hasDepth").GetBoolean(), "x-subflow-depth crossed the framework exclusions");
        Assert.False(carried.GetProperty("hasToken").GetBoolean(), "x-token-id crossed the deny floor");
        Assert.DoesNotContain(run.Canary, data.GetRawText(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ApplyToResume_Off_NextChildSeesNoHeaders()
    {
        Skip.If(ApplyToResumeOn, $"{ModeVariable}=1 — the runtime applies carried headers to the resume");

        var run = await RunManualAsync("finish");

        var data = await WaitParentAsync(run.ParentId, "C");
        Assert.Equal(run.Sub, data.GetProperty("carried").GetProperty("sub").GetString());
        Assert.True(data.GetProperty("bSettled").GetBoolean());
        Assert.True(IsNullOrEmpty(data, "bSeenSub"), $"child B saw a sub on the resume: {data.GetRawText()}");
        Assert.Equal("no-sub", data.GetProperty("resumeRoute").GetString());
    }

    [SkippableFact]
    public async Task ApplyToResume_On_NextChildSeesFinisherSub()
    {
        Skip.IfNot(ApplyToResumeOn, $"needs {ModeVariable}=1 and a runtime with ApplyToResume=true");

        var run = await RunManualAsync("finish");

        var data = await WaitParentAsync(run.ParentId, "C");
        Assert.Equal(run.Sub, data.GetProperty("bSeenSub").GetString());
        Assert.Equal("saw-sub", data.GetProperty("resumeRoute").GetString());
    }

    [Fact]
    public async Task TimerFinishedChild_CarriesNothing()
    {
        var parentId = await StartParentAsync("timer");

        var data = await WaitParentAsync(parentId, "C");
        var carried = data.GetProperty("carried");
        Assert.Equal(0, carried.GetProperty("count").GetInt32());
        Assert.True(IsNullOrEmpty(carried, "sub"), $"timer-finished child carried a sub: {carried.GetRawText()}");
        Assert.Equal("no-sub", data.GetProperty("resumeRoute").GetString());
        Assert.True(IsNullOrEmpty(data, "bSeenSub"));
    }

    /// <summary>
    /// Cancel settles the child through <c>instance.sub.canceled</c>, whose parent side
    /// (SubflowCancellationService) runs NO output mapping — so nothing reaches <c>carried</c>. The
    /// carried set is only applied to the resume, and only with ApplyToResume on.
    /// </summary>
    [Fact]
    public async Task CanceledChild_CarriesHeaders()
    {
        var run = await RunManualAsync("cancel-sfch-child");

        var data = await WaitParentAsync(run.ParentId, "C");
        Assert.Equal("sfch-child-cancelled", (await GetInstanceStateAsync(Child, run.ChildId)).State);
        Assert.False(data.TryGetProperty("carried", out _), $"cancel ran an output mapping: {data.GetRawText()}");

        if (ApplyToResumeOn)
        {
            Assert.Equal("saw-sub", data.GetProperty("resumeRoute").GetString());
            Assert.Equal(run.Sub, data.GetProperty("bSeenSub").GetString());
        }
        else
        {
            Assert.Equal("no-sub", data.GetProperty("resumeRoute").GetString());
            Assert.True(IsNullOrEmpty(data, "bSeenSub"));
        }
    }

    /// <summary>
    /// The child's own request hop faults (its <c>explode</c> task throws). The fault path runs the
    /// parent's output mapping with the carried set before the parent faults (no error boundary).
    /// </summary>
    [Fact]
    public async Task FaultedChild_CarriesHeadersToOutputMapping()
    {
        var run = await RunManualAsync("explode");

        var data = await WaitParentAsync(run.ParentId, "F");
        Assert.Equal("F", (await GetInstanceStateAsync(Child, run.ChildId)).Status);
        var carried = data.GetProperty("carried");
        Assert.Equal(run.Sub, carried.GetProperty("sub").GetString());
        Assert.Equal("v1", carried.GetProperty("custom").GetString());
        Assert.False(carried.GetProperty("hasAuthorization").GetBoolean());
        Assert.False(carried.GetProperty("hasDepth").GetBoolean());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Run(string ParentId, string ChildId, string Sub, string Canary);

    private async Task<string> StartParentAsync(string aMode)
    {
        var parentId = await StartAsync(Parent, new { aMode });
        _output.WriteLine($"parent={parentId} aMode={aMode}");
        return parentId;
    }

    /// <summary>Starts the parent, waits for child A to park, and settles it with the probe headers.</summary>
    private async Task<Run> RunManualAsync(string transition)
    {
        var parentId = await StartParentAsync("manual");

        string? childId = null;
        await WaitUntilAsync(async () =>
            {
                var active = await GetActiveSubflowsAsync(Parent, parentId);
                if (!active.TryGetValue(Child, out childId)) return false;
                if ((await GetInstanceStateAsync(Parent, parentId)).Status == "F")
                    Assert.Fail($"parent faulted before child A parked — {await DescribeAsync(Parent, parentId)}");
                try
                {
                    // The correlation (with a preassigned child id) is visible before the child row exists.
                    return (await GetInstanceStateAsync(Child, childId)) is ("sfch-child-wait", "A");
                }
                catch (HttpRequestException exception) when (exception.Message.Contains("NotFound", StringComparison.Ordinal))
                {
                    return false;
                }
            },
            $"child A never parked in sfch-child-wait — {await DescribeAsync(Parent, parentId)}", Budget);

        // `sub` is the caller-identity header: the runtime persists it in a varchar(36) column, so a
        // longer value (e.g. "finisher-<32 hex>") fails the save with 22001 → 500. A plain GUID fits.
        var sub = Guid.NewGuid().ToString();
        var canary = $"canary-{Guid.NewGuid():N}";
        _output.WriteLine($"child={childId} transition={transition} sub={sub} authorization=Bearer {canary}");

        var headers = Headers();
        headers["sub"] = sub;
        headers["x-carried-custom"] = "v1";
        headers["authorization"] = $"Bearer {canary}";
        headers["x-subflow-depth"] = "9";

        var url = $"api/v1/core/workflows/{Child}/instances/{childId}/transitions/{transition}?sync=false";
        HttpStatusCode status = default;
        string body = "";
        for (var attempt = 1; attempt <= LockContentionAttempts; attempt++)
        {
            (status, body) = await SendRawAsync(HttpMethod.Patch, url, new { }, headers);
            if (status != HttpStatusCode.Conflict || !body.Contains("Failed to acquire lock", StringComparison.Ordinal)) break;
            await Task.Delay(LockContentionDelay);
        }

        Assert.True((int)status < 400, $"'{transition}' on child {childId} was refused with {(int)status}: {body}");
        return new Run(parentId, childId!, sub, canary);
    }

    private async Task<JsonElement> WaitParentAsync(string parentId, string status)
    {
        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Parent, parentId)).Status == status,
            $"{Parent} never reached status {status} — {await DescribeAsync(Parent, parentId)}", Budget);
        var data = await GetAttributesAsync(Parent, parentId);
        _output.WriteLine($"parent={parentId} status={status} data={data.GetRawText()}");
        return data;
    }

    private static bool IsNullOrEmpty(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value)
        || value.ValueKind == JsonValueKind.Null
        || (value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString()));
}
