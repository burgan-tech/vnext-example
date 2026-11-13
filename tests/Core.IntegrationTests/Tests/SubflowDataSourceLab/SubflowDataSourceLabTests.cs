using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SubflowDataSourceLab;

/// <summary>
/// <c>subflow-data-source-lab</c>: which instance's DATA a polling client must read while the
/// parent has an active <c>S</c> SubFlow, and what the runtime gives it to do so.
/// </summary>
/// <remarks>
/// The parent's state function descends to the child (<c>state</c>, <c>view</c>, <c>transitions</c>
/// are the child's) but its <c>data.href</c> stays the PARENT's data function, whose body does not
/// move with the child (Amorphie 474774, vnext#1084: 304 for as long as the child is open). Every
/// <c>activeCorrelations[]</c> entry carries an <c>href</c> that IS the child's data function. The
/// client SDK (vnext-client-workflow-manager, WMP 482207) will read the active <c>S</c> correlation's
/// href; these tests pin that contract. Reads go out over a raw client because three of them are
/// about the status code and the <c>ETag</c> of a conditional GET.
/// </remarks>
public sealed class SubflowDataSourceLabTests : WorkflowTestBase
{
    private const string Parent = "sds-parent";
    private const string Child = "sds-child";

    private readonly HttpClient _http;

    public SubflowDataSourceLabTests(VNextTestEnvironment environment) : base(environment)
    {
        _http = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    [Fact]
    public async Task TheParentStateDescendsToTheChild_AndTheCorrelationHrefIsTheChildsData()
    {
        var parentId = await EnterChildAsync();
        var (_, _, state) = await StateAsync(parentId);

        Assert.Equal("child-wait", state.GetProperty("state").GetString());
        Assert.Equal("A", state.GetProperty("status").GetString());
        Assert.True(state.GetProperty("view").GetProperty("hasView").GetBoolean(), "the child's view is announced");

        var correlation = ActiveSubflow(state);
        Assert.NotNull(correlation);
        var childId = correlation.Value.GetProperty("subFlowInstanceId").GetString();
        Assert.Equal("core", correlation.Value.GetProperty("subFlowDomain").GetString());
        Assert.Equal(Child, correlation.Value.GetProperty("subFlowName").GetString());
        Assert.False(correlation.Value.GetProperty("isCompleted").GetBoolean());

        var href = correlation.Value.GetProperty("href").GetString() ?? "";
        Assert.Contains($"/core/workflows/{Child}/instances/{childId}/functions/data", href);
        Assert.Contains($"/core/workflows/{Parent}/instances/{parentId}/functions/data",
            DataHref(state));
    }

    [Fact]
    public async Task TheParentsDataIsTheParents_TheHrefsDataIsTheChilds()
    {
        var parentId = await EnterChildAsync();
        var (_, _, state) = await StateAsync(parentId);

        var (parentStatus, parentEtag, parentBody) = await GetAsync(DataHref(state));
        Assert.Equal(HttpStatusCode.OK, parentStatus);
        Assert.False(string.IsNullOrEmpty(parentEtag), "the parent's data answered without an ETag");
        Assert.Contains("parentMarker", parentBody);
        Assert.DoesNotContain("childMarker", parentBody);

        var (childStatus, childEtag, childBody) = await GetAsync(ChildHref(state));
        Assert.Equal(HttpStatusCode.OK, childStatus);
        Assert.False(string.IsNullOrEmpty(childEtag), "the child's data answered without an ETag");
        Assert.Contains("childMarker", childBody);
    }

    /// <summary>
    /// <c>touch</c> is a <c>$self</c> transition on the child: the child's data changes, its state
    /// does not, and the parent's own data is untouched. This is the moment 474774 / vnext#1084
    /// describe — the parent's data function keeps answering 304. Whether the parent's STATE ETag
    /// moves on a child <c>$self</c> is not documented; it is read and reported, not asserted.
    /// </summary>
    [Fact]
    public async Task ATouchChangesOnlyTheChildsData()
    {
        var parentId = await EnterChildAsync();
        var (_, stateEtag, state) = await StateAsync(parentId);
        var parentHref = DataHref(state);
        var childHref = ChildHref(state);
        var (_, parentEtag, _) = await GetAsync(parentHref);
        var (_, childEtag, _) = await GetAsync(childHref);

        await TouchAsync(parentId, childHref);

        var (childAfter, childEtagAfter, _) = await GetAsync(childHref, childEtag);
        Assert.Equal(HttpStatusCode.OK, childAfter);
        Assert.NotEqual(childEtag, childEtagAfter);

        var (parentAfter, _, _) = await GetAsync(parentHref, parentEtag);
        Assert.Equal(HttpStatusCode.NotModified, parentAfter);

        var (stateAfter, stateEtagAfter, _) = await StateAsync(parentId, stateEtag);
        Console.WriteLine($"[sds] parent state after a child $self touch: {(int)stateAfter} (ETag {stateEtag} -> {stateEtagAfter})");
    }

    [Fact]
    public async Task ANextMovesTheParentStateEtag()
    {
        var parentId = await EnterChildAsync();
        var (_, stateEtag, before) = await StateAsync(parentId);

        await SubmitAsync(Parent, parentId, "next");
        await WaitForObservedStateAsync(Parent, parentId, "child-wait-2");

        var (status, etagAfter, after) = await StateAsync(parentId, stateEtag);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual(stateEtag, etagAfter);
        Assert.Equal(ActiveSubflow(before)!.Value.GetProperty("subFlowInstanceId").GetString(),
            ActiveSubflow(after)!.Value.GetProperty("subFlowInstanceId").GetString());
    }

    [Fact]
    public async Task AfterFinishTheParentResumes_WithTheChildsOutputMerged()
    {
        var parentId = await EnterChildAsync();
        var (_, _, state) = await StateAsync(parentId);
        var parentHref = DataHref(state);
        var (_, parentEtag, _) = await GetAsync(parentHref);

        await TouchAsync(parentId, ChildHref(state));
        await SubmitAsync(Parent, parentId, "finish");
        await WaitForObservedStateAsync(Parent, parentId, "parent-after");

        var (_, _, after) = await StateAsync(parentId);
        Assert.Null(ActiveSubflow(after));

        var (status, _, body) = await GetAsync(DataHref(after), parentEtag);
        Assert.Equal(HttpStatusCode.OK, status);
        var data = Data(body);
        Assert.True(data.GetProperty("childResult").GetBoolean());
        Assert.Equal(1, data.GetProperty("childCounter").GetInt32());

        await SubmitAsync(Parent, parentId, "finish-parent");
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    /// <summary>Starts a parent, walks it into its SubFlow and waits until the child is waiting.</summary>
    private async Task<string> EnterChildAsync()
    {
        var parentId = await StartAsync(Parent, new { labRef = $"sds-{Guid.NewGuid():N}"[..16] });
        await AssertNotFaultedAsync(Parent, parentId);
        await WaitForObservedStateAsync(Parent, parentId, "parent-wait");

        await SubmitAsync(Parent, parentId, "enter-child");
        await WaitForObservedStateAsync(Parent, parentId, "child-wait");
        return parentId;
    }

    /// <summary><c>touch</c> via the parent (forwarded to the child) and wait until the child's data shows it.</summary>
    private async Task TouchAsync(string parentId, string childHref)
    {
        await SubmitAsync(Parent, parentId, "touch");
        await WaitUntilAsync(async () => await ChildCounterAsync(childHref) == 1,
            "the child's data should show childCounter == 1");
    }

    /// <summary>The parent's state function as a client polls it: status, ETag and body (empty object on 304).</summary>
    private async Task<(HttpStatusCode Status, string? ETag, JsonElement Body)> StateAsync(string parentId, string? ifNoneMatch = null)
    {
        var (status, etag, body) = await PollStateAsync(Parent, parentId, ifNoneMatch);
        if (status == HttpStatusCode.OK)
            return (status, etag, JsonDocument.Parse(body).RootElement.Clone());
        Assert.Equal(HttpStatusCode.NotModified, status);
        return (status, etag, JsonDocument.Parse("{}").RootElement.Clone());
    }

    /// <summary>
    /// A conditional GET on an arbitrary href (hrefs come host-relative with the <c>/api/v1</c> prefix).
    /// <see cref="WorkflowTestBase.PollStateAsync"/> covers only the state function and
    /// <see cref="WorkflowTestBase.SendRawAsync"/> drops the headers, hence the lab-local client.
    /// </summary>
    private async Task<(HttpStatusCode Status, string? ETag, string Body)> GetAsync(string url, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimStart('/'));
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);
        if (!string.IsNullOrEmpty(ifNoneMatch)) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        using var response = await _http.SendAsync(request);
        var etag = response.Headers.ETag?.ToString()
                   ?? (response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null);
        return (response.StatusCode, etag, await response.Content.ReadAsStringAsync());
    }

    private async Task<int> ChildCounterAsync(string childHref)
    {
        var (status, _, body) = await GetAsync(childHref);
        if (status != HttpStatusCode.OK) return -1;
        return Data(body).TryGetProperty("childCounter", out var value) && value.TryGetInt32(out var number) ? number : -1;
    }

    /// <summary>The data function's business payload — under <c>data</c> when the body is enveloped.</summary>
    private static JsonElement Data(string body)
    {
        var root = JsonDocument.Parse(body).RootElement.Clone();
        return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
    }

    private static string DataHref(JsonElement state) => state.GetProperty("data").GetProperty("href").GetString()!;

    /// <summary>The open child's data href — what the client SDK reads while the child is open.</summary>
    private static string ChildHref(JsonElement state) => ActiveSubflow(state)!.Value.GetProperty("href").GetString()!;

    /// <summary>
    /// The first open <c>S</c> correlation in wire order: the runtime lists the parent's own
    /// correlations first, then the open child's subtree, and a sequential sub-flow is only open
    /// while it blocks its parent — so this is the parent's direct child. The client SDK applies
    /// the same rule.
    /// </summary>
    private static JsonElement? ActiveSubflow(JsonElement state)
    {
        if (!state.TryGetProperty("activeCorrelations", out var correlations)) return null;
        foreach (var correlation in correlations.EnumerateArray())
        {
            if (correlation.GetProperty("subFlowType").GetString() == "S" &&
                !correlation.GetProperty("isCompleted").GetBoolean())
                return correlation;
        }
        return null;
    }
}
