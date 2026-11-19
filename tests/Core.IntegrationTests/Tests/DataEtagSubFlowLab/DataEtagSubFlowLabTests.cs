using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.DataEtagSubFlowLab;

/// <summary>
/// The data function's ETag while the polled instance waits on an active SubFlow (vnext#1084).
/// <para>
/// <c>data-etag-subflow-lab</c> sits in <c>wait-otp</c> for the whole life of its child
/// <c>data-etag-subflow-lab-child</c>; its own data and its effective state never move. The child's
/// <c>wrong-otp</c> writes data and lands back in <c>otp</c>, which changes the child's extension output
/// (<c>dataEtagSubflowLabOtpInput</c> — the shape of morph-idm's <c>extensions.otpInput</c>). The parent's
/// data function serves that extension output, so a client holding the parent's ETag must get a 200 with
/// the new output, and a 304 while nothing changed. Before the fix the ETag covered the parent alone and
/// every conditional call after the first answered 304.
/// </para>
/// </summary>
public class DataEtagSubFlowLabTests : WorkflowTestBase
{
    private const string Parent = "data-etag-subflow-lab";
    private const string Child = "data-etag-subflow-lab-child";
    private const string ExtensionKey = "dataEtagSubflowLabOtpInput";

    public DataEtagSubFlowLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>304 while unchanged → 200 with the child's new extension output → 304 again.</summary>
    [Fact]
    public async Task ChildWritesDataInSameState_ParentDataAnswers200WithNewExtensions_Then304()
    {
        var (parentId, childId) = await StartParentWaitingOnChildAsync();

        var (status, etag, body) = await PollDataAsync(Parent, parentId);
        Assert.True(status == HttpStatusCode.OK, $"first read answered {(int)status}: {body}");
        Assert.False(string.IsNullOrEmpty(etag), "first read carried no ETag");
        AssertOtpInput(body, wrongOtp: false, attempt: 0);

        var (unchanged, _, _) = await PollDataAsync(Parent, parentId, etag);
        Assert.Equal(HttpStatusCode.NotModified, unchanged);

        // The client's action reaches the child through the parent (the SubFlow transition proxy).
        await SubmitAsync(Parent, parentId, "wrong-otp", new { wrongOtp = true, attempt = 1 });
        await WaitUntilSettledAsync(Child, childId);
        await AssertChildStillInOtpAsync(childId);

        var (changed, newEtag, changedBody) = await PollDataAsync(Parent, parentId, etag);
        Assert.True(changed == HttpStatusCode.OK,
            $"the child's data changed but the parent answered {(int)changed} to the old ETag {etag}");
        Assert.NotEqual(etag, newEtag);
        AssertOtpInput(changedBody, wrongOtp: true, attempt: 1);

        var (again, _, _) = await PollDataAsync(Parent, parentId, newEtag);
        Assert.Equal(HttpStatusCode.NotModified, again);
    }

    /// <summary>
    /// The parent's ETag carries the child's data ETag verbatim after its own part, which is what lets
    /// the parent forward it to the child and answer 304 without running an extension.
    /// </summary>
    [Fact]
    public async Task ParentEtag_EndsWithTheChildsDataEtag()
    {
        var (parentId, childId) = await StartParentWaitingOnChildAsync();

        var (_, parentEtag, _) = await PollDataAsync(Parent, parentId);
        var (childStatus, childEtag, childBody) = await PollDataAsync(Child, childId);
        Assert.True(childStatus == HttpStatusCode.OK, $"child read answered {(int)childStatus}: {childBody}");

        var parentValue = parentEtag!.Trim('"');
        var childValue = childEtag!.Trim('"');
        Assert.EndsWith("." + childValue, parentValue);
        Assert.DoesNotContain('.', parentValue[..parentValue.IndexOf('.')]);
    }

    private async Task<(string ParentId, string ChildId)> StartParentWaitingOnChildAsync()
    {
        var parentId = await StartAsync(Parent, new { testId = $"data-etag-{Guid.NewGuid():N}"[..32] });

        string? childId = null;
        await WaitUntilAsync(async () =>
        {
            var subflows = await GetActiveSubflowsAsync(Parent, parentId);
            return subflows.TryGetValue(Child, out childId);
        }, $"{Parent}/{parentId} never opened its {Child} SubFlow", TimeSpan.FromSeconds(60));

        await WaitUntilSettledAsync(Child, childId!);
        await AssertNotFaultedAsync(Child, childId!);
        await AssertChildStillInOtpAsync(childId!);
        return (parentId, childId!);
    }

    private async Task AssertChildStillInOtpAsync(string childId)
    {
        var (state, _) = await GetInstanceStateAsync(Child, childId);
        Assert.Equal("otp", state);
    }

    private static void AssertOtpInput(string body, bool wrongOtp, int attempt)
    {
        var root = JsonDocument.Parse(body).RootElement;
        var otpInput = default(JsonElement);
        Assert.True(root.TryGetProperty("extensions", out var extensions)
                    && extensions.TryGetProperty(ExtensionKey, out otpInput),
            $"no extensions.{ExtensionKey} in {body}");
        Assert.Equal(wrongOtp, otpInput.GetProperty("wrongOtp").GetBoolean());
        Assert.Equal(attempt, otpInput.GetProperty("attempt").GetInt32());
    }
}
