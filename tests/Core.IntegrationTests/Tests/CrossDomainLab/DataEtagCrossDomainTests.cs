using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// DX-01..DX-03 of labs/cross-domain/VNEXT-BUILD-PLAN-data-etag-xd.md (vnext#1084 across a domain
/// boundary). The core parent <c>de-xd-parent</c> waits in <c>wait-otp</c> on the PARTNER child
/// <c>de-xd-child</c>. The child's <c>wrong-otp</c> writes data and stays in <c>otp</c>; the partner extension
/// <c>de-xd-otp-input</c> reads that data. The parent's data function reads the child through
/// <c>RemoteInstanceQueryAppService</c> (HTTP or Dapr) with the child part of the composed ETag as
/// <c>If-None-Match</c>, so the child's ETag must cross the boundary in the response and the 304 must
/// survive the transport.
/// </summary>
[Collection("VNextIntegration")]
public class DataEtagCrossDomainTests : CrossDomainLabTestBase, IClassFixture<CrossDomainLabFixture>
{
    private const string XdParent = "de-xd-parent";
    private const string XdChild = "de-xd-child";
    private const string ExtensionKey = "deXdOtpInput";

    public DataEtagCrossDomainTests(VNextTestEnvironment environment, CrossDomainLabFixture lab)
        : base(environment, lab) { }

    /// <summary>DX-01: 304 while unchanged → 200 with the partner child's new extension output → 304.</summary>
    [SkippableFact]
    public async Task PartnerChildWritesDataInSameState_ParentAnswers200WithNewExtensions_Then304()
    {
        RequirePartner();
        var (parentId, childId) = await StartParentWaitingOnPartnerChildAsync();

        var (status, etag, body) = await PollDataAsync(XdParent, parentId);
        Assert.True(status == HttpStatusCode.OK, $"first read answered {(int)status}: {body}");
        Assert.False(string.IsNullOrEmpty(etag), "first read carried no ETag (did the partner child's read fail?)");
        AssertOtpInput(body, wrongOtp: false, attempt: 0);

        var (unchanged, _, _) = await PollDataAsync(XdParent, parentId, etag);
        Assert.Equal(HttpStatusCode.NotModified, unchanged);

        // Forwarded by the parent's SubFlow proxy to the partner child.
        await SubmitAsync(XdParent, parentId, "wrong-otp", new { wrongOtp = true, attempt = 1 });
        await WaitUntilAsync(async () => (await PartnerStatusAsync(childId)) == "A",
            $"partner child {childId} never settled after wrong-otp", TimeSpan.FromSeconds(60));

        var (changed, newEtag, changedBody) = await PollDataAsync(XdParent, parentId, etag);
        Assert.True(changed == HttpStatusCode.OK,
            $"the partner child's data changed but the parent answered {(int)changed} to the old ETag {etag}");
        Assert.NotEqual(etag, newEtag);
        AssertOtpInput(changedBody, wrongOtp: true, attempt: 1);

        var (again, _, _) = await PollDataAsync(XdParent, parentId, newEtag);
        Assert.Equal(HttpStatusCode.NotModified, again);
    }

    /// <summary>DX-02: the parent's ETag ends with the partner child's own data ETag.</summary>
    [SkippableFact]
    public async Task ParentEtag_EndsWithThePartnerChildsDataEtag()
    {
        RequirePartner();
        var (parentId, childId) = await StartParentWaitingOnPartnerChildAsync();

        var (_, parentEtag, _) = await PollDataAsync(XdParent, parentId);
        var (childStatus, childEtag, childBody) = await PollPartnerDataAsync(childId);
        Assert.True(childStatus == HttpStatusCode.OK, $"partner child read answered {(int)childStatus}: {childBody}");

        var parentValue = parentEtag!.Trim('"');
        Assert.EndsWith("." + childEtag!.Trim('"'), parentValue);
    }

    private async Task<(string ParentId, string ChildId)> StartParentWaitingOnPartnerChildAsync()
    {
        var parentId = await StartAsync(XdParent, new { testId = NewTestId("de-xd") });

        string? childId = null;
        await WaitUntilAsync(async () =>
        {
            var subflows = await GetActiveSubflowsAsync(XdParent, parentId);
            return subflows.TryGetValue(XdChild, out childId);
        }, $"{XdParent}/{parentId} never opened its partner {XdChild} SubFlow", TimeSpan.FromSeconds(60));

        await WaitUntilAsync(async () => (await PartnerStatusAsync(childId!)) == "A",
            $"partner child {childId} never settled", TimeSpan.FromSeconds(60));
        return (parentId, childId!);
    }

    private async Task<string?> PartnerStatusAsync(string childId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(Lab.PartnerBaseUrl!.TrimEnd('/') + "/") };
        using var response = await client.GetAsync($"api/v1/partner/workflows/{XdChild}/instances/{childId}");
        if (!response.IsSuccessStatusCode) return null;
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var metadata = root.GetProperty("metadata");
        return metadata.GetProperty("currentState").GetString() == "otp" ? metadata.GetProperty("status").GetString() : null;
    }

    private async Task<(HttpStatusCode Status, string? ETag, string Body)> PollPartnerDataAsync(string childId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(Lab.PartnerBaseUrl!.TrimEnd('/') + "/") };
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"api/v1/partner/workflows/{XdChild}/instances/{childId}/functions/data");
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await client.SendAsync(request);
        var etag = response.Headers.ETag?.ToString()
                   ?? (response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null);
        return (response.StatusCode, etag, await response.Content.ReadAsStringAsync());
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
