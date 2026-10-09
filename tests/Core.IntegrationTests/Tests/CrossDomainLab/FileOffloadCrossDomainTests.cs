using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// XS-01..XS-05 of labs/cross-domain/VNEXT-BUILD-PLAN-file-offload.md (vnext-client-sdk-core#101): the x-storage
/// file offload across a domain boundary. A core parent (<c>fo-xd-parent</c>) whose active SubFlow is the partner
/// <c>fo-xd-child</c> receives an upload; the parent forwards the body untouched and the PARTNER leaf swaps the
/// <c>content</c> for a handle against its own master schema and its own sidecar binding. A core script then reads
/// the bytes back through <c>GetFileAsync("partner", …)</c> — the cross-domain <c>internal/file</c> path.
/// <para>
/// XS-06 (the object sits in the partner sidecar's store, not core's) is a manual <c>docker cp</c> check — see
/// the CrossDomainLab README.
/// </para>
/// </summary>
[Collection("VNextIntegration")]
public class FileOffloadCrossDomainTests : CrossDomainLabTestBase, IClassFixture<CrossDomainLabFixture>
{
    private const string XsParent = "fo-xd-parent";
    private const string XsChild = "fo-xd-child";
    private const string XsReader = "fo-xd-reader";
    private const string Binding = "vnext-blob-local";
    private const string Pdf = "application/pdf";

    private readonly HttpClient _core;
    private readonly HttpClient? _partner;

    public FileOffloadCrossDomainTests(VNextTestEnvironment environment, CrossDomainLabFixture lab) : base(environment, lab)
    {
        // Raw clients: status codes and the functions/file bytes are asserted directly.
        _core = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
        if (lab.PartnerBaseUrl is not null)
            _partner = new HttpClient { BaseAddress = new Uri(lab.PartnerBaseUrl.TrimEnd('/') + "/") };
    }

    private HttpClient Partner => _partner ?? throw new InvalidOperationException("partner url is not set");

    /// <summary>XS-01: enter-child starts fo-xd-child in partner; the core parent's state function shows the leaf.</summary>
    [SkippableFact]
    public async Task EnterChild_StartsTheChildInPartner()
    {
        RequirePartner();
        var (parentId, childId) = await StartParentWithChildAsync();

        var (status, raw) = await SendAsync(_core, HttpMethod.Get,
            $"api/v1/core/workflows/{XsParent}/instances/{parentId}/functions/state");
        Assert.True(status == HttpStatusCode.OK, $"state answered {(int)status}: {raw}");
        var state = Parse(raw);
        Assert.Equal("child-waiting", Str(state, "state"));
        var correlation = state.GetProperty("activeCorrelations").EnumerateArray().First();
        Assert.Equal("partner", Str(correlation, "subFlowDomain"));
        Assert.Equal(childId, Str(correlation, "subFlowInstanceId"));

        var childData = await DataAsync(Partner, "partner", XsChild, childId);
        Assert.Equal(parentId, Str(childData, "parentInstanceId"));
    }

    /// <summary>XS-02: a sync upload to the core parent is 200 with the parent id; the partner leaf owns the handle.</summary>
    [SkippableFact]
    public async Task SyncUpload_ThroughCoreParent_PartnerLeafOwnsTheHandle()
    {
        RequirePartner();
        var (parentId, childId) = await StartParentWithChildAsync();
        var bytes = Bytes(4096);

        var (status, body) = await UploadThroughParentAsync(parentId, "xd-sync.pdf", bytes, sync: true);

        Assert.True(status == HttpStatusCode.OK, $"forwarded child-upload answered {(int)status}: {body}");
        Assert.Equal(parentId, Id(body));
        Assert.DoesNotContain("\"content\"", body, StringComparison.Ordinal);
        await AssertPartnerChildOwnsTheFileAsync(parentId, childId, bytes, "xd-sync.pdf");
    }

    /// <summary>XS-03: the same upload async is 202 with the parent id; after the rest the leaf owns the handle.</summary>
    [SkippableFact]
    public async Task AsyncUpload_ThroughCoreParent_Is202_AndThePartnerLeafOwnsTheHandle()
    {
        RequirePartner();
        var (parentId, childId) = await StartParentWithChildAsync();
        var bytes = Bytes(2048);

        var (status, body) = await UploadThroughParentAsync(parentId, "xd-async.pdf", bytes, sync: false);

        Assert.True(status == HttpStatusCode.Accepted, $"forwarded child-upload answered {(int)status}: {body}");
        Assert.Equal(parentId, Id(body));
        Assert.DoesNotContain("\"content\"", body, StringComparison.Ordinal);
        await WaitUntilAsync(async () => (await DataAsync(Partner, "partner", XsChild, childId)).TryGetProperty("passport", out _),
            $"partner child {childId} never received the passport", TimeSpan.FromSeconds(60));
        await WaitForPartnerStatusAsync(childId, "A");
        await AssertPartnerChildOwnsTheFileAsync(parentId, childId, bytes, "xd-async.pdf");
    }

    /// <summary>
    /// XS-04: partner's functions/file serves the bytes (SHA-256 = eTag); the core parent's functions/file with the
    /// same id is 404 — the file function does not descend into the SubFlow.
    /// </summary>
    [SkippableFact]
    public async Task FileFunction_PartnerServesTheBytes_CoreParentDoesNotDescend()
    {
        RequirePartner();
        var (parentId, childId) = await StartParentWithChildAsync();
        var bytes = Bytes(3000);
        var (status, body) = await UploadThroughParentAsync(parentId, "xd-read.pdf", bytes, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"forwarded child-upload answered {(int)status}: {body}");
        var handle = (await DataAsync(Partner, "partner", XsChild, childId)).GetProperty("passport");
        var file = handle.GetProperty("file").GetString()!;

        using (var fromPartner = await FileAsync(Partner, "partner", XsChild, childId, file))
        {
            var read = await fromPartner.Content.ReadAsByteArrayAsync();
            Assert.Equal(HttpStatusCode.OK, fromPartner.StatusCode);
            Assert.Equal(handle.GetProperty("eTag").GetString(), Sha(read));
            Assert.Equal(Sha(bytes), Sha(read));
            Assert.Equal($"\"{handle.GetProperty("eTag").GetString()}\"", fromPartner.Headers.ETag?.Tag);
            Assert.Equal(Pdf, fromPartner.Content.Headers.ContentType?.MediaType);
        }

        using (var fromCoreParent = await FileAsync(_core, "core", XsParent, parentId, file))
        {
            var text = await fromCoreParent.Content.ReadAsStringAsync();
            Assert.True(fromCoreParent.StatusCode == HttpStatusCode.NotFound,
                $"core parent functions/file answered {(int)fromCoreParent.StatusCode}: {text}");
            Assert.Contains("Instance:100049", text, StringComparison.Ordinal);
        }
    }

    /// <summary>XS-05: a core script reads the partner file through GetFileAsync("partner", …); its SHA-256 = eTag.</summary>
    [SkippableFact]
    public async Task GetFileAsync_FromCore_ReadsThePartnerFile()
    {
        RequirePartner();
        var (parentId, childId) = await StartParentWithChildAsync();
        var bytes = Bytes(8192);
        var (status, body) = await UploadThroughParentAsync(parentId, "xd-remote.pdf", bytes, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"forwarded child-upload answered {(int)status}: {body}");
        var handle = (await DataAsync(Partner, "partner", XsChild, childId)).GetProperty("passport");

        (status, body) = await SendJsonAsync(_core, HttpMethod.Post,
            $"api/v1/core/workflows/{XsReader}/instances/start?sync=true", new { attributes = new { note = "reader" } });
        Assert.True(status == HttpStatusCode.OK, $"reader start answered {(int)status}: {body}");
        var readerId = Id(body);

        (status, body) = await SendJsonAsync(_core, HttpMethod.Patch,
            $"api/v1/core/workflows/{XsReader}/instances/{readerId}/transitions/read?sync=true",
            new
            {
                attributes = new
                {
                    sourceDomain = "partner", sourceFlow = XsChild, sourceInstance = childId,
                    sourceFileId = handle.GetProperty("file").GetString(),
                }
            });
        Assert.True(status == HttpStatusCode.OK, $"read answered {(int)status}: {body}");
        await WaitForInstanceStateAsync(XsReader, readerId, "r-read", timeout: TimeSpan.FromSeconds(60));
        await AssertNotFaultedAsync(XsReader, readerId);

        var data = await DataAsync(_core, "core", XsReader, readerId);
        Assert.Equal(handle.GetProperty("eTag").GetString(), Str(data, "checksum"));
        Assert.Equal(Sha(bytes), Str(data, "checksum"));
        Assert.Equal(bytes.Length, data.GetProperty("checksumSize").GetInt32());
        Assert.Equal("xd-remote.pdf", Str(data, "readName"));
        Assert.Equal(Pdf, Str(data, "readMimeType"));
        Assert.Equal(handle.GetProperty("eTag").GetString(), Str(data, "readETag"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Starts fo-xd-parent, fires enter-child and waits until the partner child rests in child-waiting.</summary>
    private async Task<(string ParentId, string ChildId)> StartParentWithChildAsync()
    {
        var (status, body) = await SendJsonAsync(_core, HttpMethod.Post,
            $"api/v1/core/workflows/{XsParent}/instances/start?sync=true", new { attributes = new { note = "xd-parent" } });
        Assert.True(status == HttpStatusCode.OK, $"parent start answered {(int)status}: {body}");
        var parentId = Id(body);
        await WaitForInstanceStateAsync(XsParent, parentId, "p-hub");
        await WaitUntilSettledAsync(XsParent, parentId);

        (status, body) = await SendJsonAsync(_core, HttpMethod.Patch,
            $"api/v1/core/workflows/{XsParent}/instances/{parentId}/transitions/enter-child?sync=true",
            new { attributes = new { } });
        Assert.True(status == HttpStatusCode.OK, $"enter-child answered {(int)status}: {body}");

        await WaitForObservedStateAsync(XsParent, parentId, "child-waiting", timeout: TimeSpan.FromSeconds(60));
        var subflows = await GetActiveSubflowsAsync(XsParent, parentId);
        Assert.True(subflows.TryGetValue(XsChild, out var childId),
            $"parent {parentId} has no active correlation for '{XsChild}': {string.Join(",", subflows.Keys)}");
        await WaitForPartnerStatusAsync(childId!, "A");
        return (parentId, childId!);
    }

    private Task WaitForPartnerStatusAsync(string childId, string expected) =>
        WaitUntilAsync(async () =>
        {
            var (http, _, status, _) = await GetPartnerInstanceAsync(XsChild, childId);
            Assert.NotEqual("F", status);
            return (int)http < 400 && status == expected;
        }, $"partner child {childId} never reached status {expected}", TimeSpan.FromSeconds(60));

    private Task<(HttpStatusCode Status, string Body)> UploadThroughParentAsync(
        string parentId, string name, byte[] bytes, bool sync) =>
        SendJsonAsync(_core, HttpMethod.Patch,
            $"api/v1/core/workflows/{XsParent}/instances/{parentId}/transitions/child-upload?sync={(sync ? "true" : "false")}",
            new { attributes = new { passport = Upload(name, Pdf, bytes) } });

    private async Task AssertPartnerChildOwnsTheFileAsync(string parentId, string childId, byte[] bytes, string name)
    {
        var childData = await DataAsync(Partner, "partner", XsChild, childId);
        var handle = childData.GetProperty("passport");
        Assert.False(handle.TryGetProperty("content", out _), $"content kept: {handle}");
        Assert.Equal(Binding, handle.GetProperty("component").GetString());
        Assert.True(Guid.TryParseExact(handle.GetProperty("file").GetString(), "D", out _), $"file is not a D-GUID: {handle}");
        Assert.Equal(bytes.Length, handle.GetProperty("size").GetInt64());
        Assert.Equal(Sha(bytes), handle.GetProperty("eTag").GetString());
        Assert.Equal(name, handle.GetProperty("name").GetString());
        Assert.Equal(Pdf, handle.GetProperty("mimeType").GetString());
        var owner = handle.GetProperty("owner");
        Assert.Equal("partner", owner.GetProperty("domain").GetString());
        Assert.Equal(XsChild, owner.GetProperty("flow").GetString());
        Assert.Equal(childId, owner.GetProperty("instance").GetString());
        Console.WriteLine($"[FileOffloadCrossDomain] parent {parentId} child {childId} handle {handle.GetRawText()}");

        var file = handle.GetProperty("file").GetString()!;
        Assert.DoesNotContain("\"content\"", await HistoryRawAsync(Partner, "partner", XsChild, childId), StringComparison.Ordinal);

        var parentData = (await DataAsync(_core, "core", XsParent, parentId)).GetRawText();
        Assert.DoesNotContain("\"content\"", parentData, StringComparison.Ordinal);
        Assert.DoesNotContain("passport", parentData, StringComparison.Ordinal);
        Assert.DoesNotContain(file, parentData, StringComparison.Ordinal);
        var parentHistory = await HistoryRawAsync(_core, "core", XsParent, parentId);
        Assert.DoesNotContain("\"content\"", parentHistory, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(bytes)[..32], parentHistory, StringComparison.Ordinal);
    }

    private async Task<JsonElement> DataAsync(HttpClient client, string domain, string workflow, string id)
    {
        var (status, raw) = await SendAsync(client, HttpMethod.Get,
            $"api/v1/{domain}/workflows/{workflow}/instances/{id}/functions/data");
        Assert.True(status == HttpStatusCode.OK, $"{domain} data function answered {(int)status}: {raw}");
        return Parse(raw).GetProperty("data").Clone();
    }

    private async Task<string> HistoryRawAsync(HttpClient client, string domain, string workflow, string id)
    {
        var (status, raw) = await SendAsync(client, HttpMethod.Get,
            $"api/v1/{domain}/workflows/{workflow}/instances/{id}/transitions");
        Assert.True(status == HttpStatusCode.OK, $"{domain} history answered {(int)status}: {raw}");
        return raw;
    }

    private async Task<HttpResponseMessage> FileAsync(HttpClient client, string domain, string workflow, string id, string file)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/v1/{domain}/workflows/{workflow}/instances/{id}/functions/file?file={file}");
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);
        return await client.SendAsync(request);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, HttpMethod method, string url)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, string Body)> SendJsonAsync(HttpClient client, HttpMethod method, string url, object payload)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
        };
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static object Upload(string name, string mimeType, byte[] bytes) =>
        new { name, mimeType, size = bytes.Length, content = Convert.ToBase64String(bytes) };

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Id(string body) =>
        Str(Parse(body), "id") ?? throw new InvalidOperationException($"no id in {body}");
}
