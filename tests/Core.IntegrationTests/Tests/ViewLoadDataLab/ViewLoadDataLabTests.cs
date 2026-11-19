using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ViewLoadDataLab;

/// <summary>
/// view-load-data-lab: the state function's <c>transitions[].view.loadData</c> is the
/// <c>loadData</c> declared on each transition's view binding (burgan-tech/vnext#1266, reported as
/// burgan-tech/vnext-client-sdk-core#120). A client opens a transition form pre-filled only when the
/// flow author asked for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before the fix</b> a transition outside an active subflow always answered <c>true</c> — so a
/// form declared <c>loadData: false</c> opened with the record's stale values — and a parent-added
/// shared transition listed during the subflow window always answered <c>false</c>. A subflow's own
/// entries were passed through from the child's state function, so they carried the same
/// <c>true</c>.
/// </para>
/// <para>
/// An undeclared binding and a transition without a view keep the value each path answered before.
/// With several entries the state function cannot evaluate their rules, so the first entry decides.
/// </para>
/// </remarks>
public class ViewLoadDataLabTests : WorkflowTestBase
{
    private const string Lab = "view-load-data-lab";

    public ViewLoadDataLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// Outside a subflow every transition's view link carries its binding's declaration; undeclared
    /// and view-less transitions answer as before.
    /// </summary>
    [Fact]
    public async Task StateFunction_TransitionViewLinks_CarryTheBindingsLoadData()
    {
        var instanceId = await StartAsync(Lab, new { testId = TestId("own") });
        var transitions = Transitions(await StateBodyAsync(instanceId));

        AssertView(transitions["vld-load-false"], hasView: true, loadData: false);
        AssertView(transitions["vld-load-true"], hasView: true, loadData: true);
        AssertView(transitions["vld-load-undeclared"], hasView: true, loadData: true);
        AssertView(transitions["vld-no-view"], hasView: false, loadData: true);
        // Two entries, the first (rule-gated) declares false: the first entry decides.
        AssertView(transitions["vld-multi"], hasView: true, loadData: false);
        // A shared transition outside a subflow takes the same path as the state's own.
        AssertView(transitions["vld-shared-note"], hasView: true, loadData: true);
    }

    /// <summary>
    /// During the subflow window the child's own entries carry the child's declarations, and the
    /// parent-added shared transition carries the parent binding's.
    /// </summary>
    [Fact]
    public async Task StateFunction_DuringSubFlow_ChildAndParentAddedEntriesCarryTheirBindingsLoadData()
    {
        var instanceId = await StartAsync(Lab, new { testId = TestId("sub") });
        // Not RunAcceptedAsync: a parent in a SubFlow state stays Busy for the child's whole lifetime.
        var accepted = await RunAsync(Lab, instanceId, "vld-ask-sub");
        Assert.True((int)accepted < 400, $"vld-ask-sub answered {(int)accepted}");
        await WaitForObservedStateAsync(Lab, instanceId, "child-review");

        var body = await StateBodyAsync(instanceId);
        Assert.Equal("child-review", body.GetProperty("state").GetString());
        var transitions = Transitions(body);

        AssertView(transitions["child-load-false"], hasView: true, loadData: false);
        AssertView(transitions["child-load-true"], hasView: true, loadData: true);
        Assert.Equal("sharedTransition", transitions["vld-shared-note"].GetProperty("kind").GetString());
        AssertView(transitions["vld-shared-note"], hasView: true, loadData: true);
    }

    private static Dictionary<string, JsonElement> Transitions(JsonElement body) =>
        body.GetProperty("transitions").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!);

    private static void AssertView(JsonElement transition, bool hasView, bool loadData)
    {
        var name = transition.GetProperty("name").GetString();
        var view = transition.GetProperty("view");
        Assert.True(view.GetProperty("hasView").GetBoolean() == hasView, $"{name}: hasView {view}");
        Assert.True(view.GetProperty("loadData").GetBoolean() == loadData, $"{name}: loadData {view}");
    }

    private async Task<JsonElement> StateBodyAsync(string instanceId)
    {
        var (status, _, body) = await PollStateAsync(Lab, instanceId);
        Assert.True(status == HttpStatusCode.OK, $"state function answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string TestId(string tag) => $"vld-{tag}-{Guid.NewGuid():N}"[..32];
}
