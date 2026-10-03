using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.DisplayLabelsLab;

/// <summary>
/// display-labels-lab: every read surface a client renders hands over the display text of what it
/// describes, so the client never shows an internal key and never reads the workflow definition to
/// find one (burgan-tech/vnext-client-sdk-core#43).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is asserted.</b> The state function: <c>stateType</c> / <c>stateSubType</c> /
/// <c>stateLabels</c> of the displayed state; on every <c>transitions[]</c> entry its own
/// <c>labels</c> and a <c>target</c> object (<c>key</c>, <c>stateType</c>, <c>stateSubType</c>,
/// <c>labels</c>, <c>subFlow</c>), <c>$self</c> resolved; the workflow <c>timeout</c> block's
/// <c>target</c> in the same shape. Followed from the state body's own links: the view, schema and
/// master functions' <c>labels</c>, and each <c>catalog</c> entry's <c>labels</c>.
/// </para>
/// <para>
/// <b>The subflow window.</b> While the child runs, the parent's body describes the CHILD's state —
/// its sub type and labels — and passes the child's own transition entries through unchanged.
/// </para>
/// <para>
/// Every label list is the definition's own <c>[{ language, label }]</c> form — both languages,
/// never one resolved for the caller.
/// </para>
/// </remarks>
public class DisplayLabelsLabTests : WorkflowTestBase
{
    private const string Lab = "display-labels-lab";

    public DisplayLabelsLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// The displayed state and every transition target are described with one vocabulary — key,
    /// type, sub type, labels — and the timeout block's target uses the same object.
    /// </summary>
    [Fact]
    public async Task StateFunction_DescribesTheCurrentStateTransitionsAndTargets()
    {
        var instanceId = await StartAsync(Lab, new { testId = TestId("state") });
        var body = await StateBodyAsync(instanceId);

        Assert.Equal("dl-review", body.GetProperty("state").GetString());
        Assert.Equal("initial", body.GetProperty("stateType").GetString());
        Assert.Equal("human", body.GetProperty("stateSubType").GetString());
        AssertLabels(body.GetProperty("stateLabels"), "İnceleme", "Review");

        var transitions = body.GetProperty("transitions").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!);

        var approve = transitions["dl-approve"];
        AssertLabels(approve.GetProperty("labels"), "Onayla", "Approve");
        var approveTarget = approve.GetProperty("target");
        Assert.Equal("dl-approved", approveTarget.GetProperty("key").GetString());
        Assert.Equal("finish", approveTarget.GetProperty("stateType").GetString());
        Assert.Equal("success", approveTarget.GetProperty("stateSubType").GetString());
        AssertLabels(approveTarget.GetProperty("labels"), "Onaylandı", "Approved");
        Assert.False(approveTarget.TryGetProperty("subFlow", out _));

        var askSubTarget = transitions["dl-ask-sub"].GetProperty("target");
        Assert.Equal("dl-sub", askSubTarget.GetProperty("key").GetString());
        Assert.Equal("subFlow", askSubTarget.GetProperty("stateType").GetString());
        Assert.Equal("none", askSubTarget.GetProperty("stateSubType").GetString());
        Assert.Equal($"{Lab}-child", askSubTarget.GetProperty("subFlow").GetString());
        AssertLabels(askSubTarget.GetProperty("labels"), "Alt Akışta", "In Child Flow");

        // $self is resolved to the state the shared transition is listed in.
        var note = transitions["dl-note"];
        Assert.Equal("sharedTransition", note.GetProperty("kind").GetString());
        AssertLabels(note.GetProperty("labels"), "Not ekle", "Add note");
        Assert.Equal("dl-review", note.GetProperty("target").GetProperty("key").GetString());
        Assert.Equal("human", note.GetProperty("target").GetProperty("stateSubType").GetString());

        var timeout = body.GetProperty("timeout");
        Assert.Equal("dl-abandoned", timeout.GetProperty("key").GetString());
        var timeoutTarget = timeout.GetProperty("target");
        Assert.Equal(JsonValueKind.Object, timeoutTarget.ValueKind);
        Assert.Equal("dl-expired", timeoutTarget.GetProperty("key").GetString());
        Assert.Equal("finish", timeoutTarget.GetProperty("stateType").GetString());
        Assert.Equal("timeout", timeoutTarget.GetProperty("stateSubType").GetString());
        AssertLabels(timeoutTarget.GetProperty("labels"), "Süresi Doldu", "Expired");
    }

    /// <summary>
    /// The view, schema, master and catalog functions — reached through the links the state body
    /// hands out — each return the labels of the component they describe.
    /// </summary>
    [Fact]
    public async Task LinkedFunctions_ReturnTheirComponentsLabels()
    {
        var instanceId = await StartAsync(Lab, new { testId = TestId("links") });
        var body = await StateBodyAsync(instanceId);

        var view = await FollowAsync(body.GetProperty("view").GetProperty("href").GetString()!);
        Assert.Equal($"{Lab}-review-view", view.GetProperty("key").GetString());
        AssertLabels(view.GetProperty("labels"), "İnceleme Ekranı", "Review Screen");

        var approve = body.GetProperty("transitions").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "dl-approve");
        Assert.True(approve.GetProperty("schema").GetProperty("hasSchema").GetBoolean());
        var schema = await FollowAsync(approve.GetProperty("schema").GetProperty("href").GetString()!);
        Assert.Equal($"{Lab}-decision", schema.GetProperty("key").GetString());
        AssertLabels(schema.GetProperty("labels"), "Onay Formu", "Approval Form");

        var master = await FollowAsync(body.GetProperty("master").GetProperty("href").GetString()!);
        Assert.Equal($"{Lab}-master", master.GetProperty("key").GetString());
        AssertLabels(master.GetProperty("labels"), "Etiket Lab Verisi", "Display Labels Lab Data");

        Assert.True(body.GetProperty("functions").GetProperty("hasFunctions").GetBoolean());
        var catalog = await FollowAsync(body.GetProperty("functions").GetProperty("href").GetString()!);
        var echo = catalog.GetProperty("functions").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "til-cached-echo");
        var echoLabels = echo.GetProperty("labels").EnumerateArray()
            .ToDictionary(l => l.GetProperty("language").GetString()!, l => l.GetProperty("label").GetString());
        Assert.Equal("Task Invocation Lab — Cached Echo", echoLabels["en-US"]);
        Assert.Equal("Task Invocation Lab — Onbellekli Echo", echoLabels["tr-TR"]);
    }

    /// <summary>
    /// During the subflow window the parent's body describes the child's state, and the child's own
    /// transition entries arrive with the labels and target the child described them with.
    /// </summary>
    [Fact]
    public async Task StateFunction_DuringSubFlow_DescribesTheChildsState()
    {
        var instanceId = await StartAsync(Lab, new { testId = TestId("sub") });
        // Not RunAcceptedAsync: a parent in a SubFlow state stays Busy for the child's whole
        // lifetime by design, so "wait until not Busy" would wait for the child to finish.
        var accepted = await RunAsync(Lab, instanceId, "dl-ask-sub");
        Assert.True((int)accepted < 400, $"dl-ask-sub answered {(int)accepted}");
        await WaitForObservedStateAsync(Lab, instanceId, "child-review");

        var body = await StateBodyAsync(instanceId);

        Assert.Equal("child-review", body.GetProperty("state").GetString());
        Assert.Equal("human", body.GetProperty("stateSubType").GetString());
        AssertLabels(body.GetProperty("stateLabels"), "Alt İnceleme", "Child Review");

        var done = body.GetProperty("transitions").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "child-done");
        AssertLabels(done.GetProperty("labels"), "Alt işi bitir", "Finish child work");
        var target = done.GetProperty("target");
        Assert.Equal("child-finished", target.GetProperty("key").GetString());
        Assert.Equal("finish", target.GetProperty("stateType").GetString());
        Assert.Equal("success", target.GetProperty("stateSubType").GetString());
        AssertLabels(target.GetProperty("labels"), "Alt Akış Tamamlandı", "Child Finished");
    }

    private async Task<JsonElement> StateBodyAsync(string instanceId)
    {
        var (status, _, body) = await PollStateAsync(Lab, instanceId);
        Assert.True(status == HttpStatusCode.OK, $"state function answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Follows a link from a response body, as a client would.</summary>
    private async Task<JsonElement> FollowAsync(string href)
    {
        // Only an http(s) href is absolute here: on Unix "/api/..." also parses as a file:// URI.
        var path = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(href).PathAndQuery : href;
        var (status, body) = await SendRawAsync(HttpMethod.Get, path, headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"{href} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Both languages, in the definition's own [{ language, label }] form.</summary>
    private static void AssertLabels(JsonElement labels, string tr, string en)
    {
        var byLanguage = labels.EnumerateArray()
            .ToDictionary(l => l.GetProperty("language").GetString()!, l => l.GetProperty("label").GetString());
        Assert.Equal(2, byLanguage.Count);
        Assert.Equal(tr, byLanguage["tr-TR"]);
        Assert.Equal(en, byLanguage["en-US"]);
    }

    private static string TestId(string tag) => $"labels-{tag}-{Guid.NewGuid():N}"[..32];
}
