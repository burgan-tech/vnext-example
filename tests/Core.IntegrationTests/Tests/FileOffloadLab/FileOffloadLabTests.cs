using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.FileOffloadLab;

/// <summary>
/// file-offload-lab (vnext-client-sdk-core#101, runtime half): a master-schema field marked <c>x-storage</c> keeps
/// its bytes in a Dapr output binding (<c>vnext-blob-local</c>, <c>bindings.localstorage</c>). The client sends the
/// file once as base64 <c>content</c>; from then on the instance data, the transition record and every read carry a
/// small handle (<c>component</c>, <c>file</c> GUID, <c>size</c>, <c>eTag</c> = SHA-256 hex, <c>owner</c>), and the bytes
/// come back only through <c>functions/file</c> or <c>ScriptBase.GetFileAsync</c>.
/// <para>
/// Pins the write rules (start sync/async, async transition, echo/reference checks, 400/503 codes, SubFlow forward,
/// transition-mapping relocation) and the read contract (headers, conditional/range reads, queryRoles 403,
/// x-roles 404, ownership 404). Row sizes and the blob folder are the manual Postgres/ls step in the README.
/// </para>
/// </summary>
public class FileOffloadLabTests : WorkflowTestBase
{
    private const string Flow = "fo-flow";
    private const string Parent = "fo-parent";
    private const string Child = "fo-child";
    private const string MissingBinding = "fo-missing-binding";
    private const string Binding = "vnext-blob-local";
    private const string Pdf = "application/pdf";
    private const string TurkishName = "kimlik-ön.pdf";

    private readonly HttpClient _http;

    public FileOffloadLabTests(VNextTestEnvironment environment) : base(environment)
    {
        // Own client: functions/file needs the response's content headers, HEAD, Range and If-None-Match — the SDK
        // client and WorkflowTestBase.SendRawAsync expose only the body as a string.
        _http = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    // ── write path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StartSync_ContentBecomesAHandle_AndNoRecordCarriesTheBytes()
    {
        var bytes = Bytes(4096);
        var (status, body) = await StartAsync(new { note = "sync", passport = Upload(TurkishName, Pdf, bytes) }, sync: true);

        Assert.True(status == HttpStatusCode.OK, $"start answered {(int)status}: {body}");
        var id = Id(body);
        Assert.DoesNotContain("\"content\"", body, StringComparison.Ordinal);

        var data = await DataAsync(Flow, id);
        AssertHandle(data.GetProperty("passport"), bytes, Flow, id, TurkishName, Pdf);
        Assert.DoesNotContain("\"content\"", data.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", await HistoryRawAsync(Flow, id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_ContentBecomesAHandle_AndNoRecordCarriesTheBytes()
    {
        var bytes = Bytes(2048);
        var (status, body) = await StartAsync(new { note = "async", passport = Upload("a.pdf", Pdf, bytes) }, sync: false);

        Assert.True(status == HttpStatusCode.Accepted, $"start answered {(int)status}: {body}");
        Assert.DoesNotContain("\"content\"", body, StringComparison.Ordinal);
        var id = Id(body);
        await WaitUntilSettledAsync(Flow, id);

        var data = await DataAsync(Flow, id);
        AssertHandle(data.GetProperty("passport"), bytes, Flow, id, "a.pdf", Pdf);
        Assert.DoesNotContain("\"content\"", data.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", await HistoryRawAsync(Flow, id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncTransition_With3MbFile_Is202_AndRestsWithAHandle()
    {
        var id = await StartPlainAsync();
        var bytes = Bytes(3 * 1024 * 1024);

        var (status, body) = await TransitionAsync(Flow, id, "add-document",
            new { files = new[] { Upload("scan.pdf", Pdf, bytes) } }, sync: false);

        Assert.True(status == HttpStatusCode.Accepted, $"add-document answered {(int)status}: {body}");
        Assert.Equal(id, Id(body));
        await WaitUntilSettledAsync(Flow, id, timeout: TimeSpan.FromSeconds(60));

        var data = await DataAsync(Flow, id);
        var file = data.GetProperty("files")[0];
        AssertHandle(file, bytes, Flow, id, "scan.pdf", Pdf);
        Assert.DoesNotContain("\"content\"", data.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", await HistoryRawAsync(Flow, id), StringComparison.Ordinal);

        var (fileStatus, read) = await ReadFileBytesAsync(Flow, id, file.GetProperty("file").GetString()!);
        Assert.Equal(HttpStatusCode.OK, fileStatus);
        Assert.Equal(Sha(bytes), Sha(read));
    }

    [Fact]
    public async Task Echo_TheStoredHandle_KeepsTheStoredMetadata()
    {
        var bytes = Bytes(512);
        var (id, stored) = await StartWithPassportAsync(bytes);

        // Full echo: everything the client read, sent back.
        var echo = JsonSerializer.Deserialize<Dictionary<string, object?>>(stored.GetRawText())!;
        var (status, body) = await TransitionAsync(Flow, id, "add-document", new { passport = echo }, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"full echo answered {(int)status}: {body}");
        AssertSameHandle(stored, (await DataAsync(Flow, id)).GetProperty("passport"));

        // Reference only.
        (status, body) = await TransitionAsync(Flow, id, "add-document",
            new { passport = new { file = stored.GetProperty("file").GetString() } }, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"file-only reference answered {(int)status}: {body}");
        AssertSameHandle(stored, (await DataAsync(Flow, id)).GetProperty("passport"));

        // Client-side metadata is discarded: the stored mimeType (and size/name/eTag) win.
        (status, body) = await TransitionAsync(Flow, id, "add-document",
            new
            {
                passport = new
                {
                    file = stored.GetProperty("file").GetString(), mimeType = "text/html", name = "evil.html", size = 1,
                    eTag = new string('0', 64),
                }
            }, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"tampered echo answered {(int)status}: {body}");
        var after = (await DataAsync(Flow, id)).GetProperty("passport");
        AssertSameHandle(stored, after);
        Assert.Equal(Pdf, after.GetProperty("mimeType").GetString());
    }

    [Fact]
    public async Task Reference_ToAnotherInstancesFile_Is400()
    {
        var (_, foreign) = await StartWithPassportAsync(Bytes(64));
        var id = await StartPlainAsync();

        var (status, body) = await TransitionAsync(Flow, id, "add-document",
            new { passport = new { file = foreign.GetProperty("file").GetString() } }, sync: true);

        AssertError(HttpStatusCode.BadRequest, "Instance:100048", status, body);
        Assert.False((await DataAsync(Flow, id)).TryGetProperty("passport", out _), "a refused reference was written");
    }

    [Fact]
    public async Task ContentAndFile_Together_Is400()
    {
        var (id, stored) = await StartWithPassportAsync(Bytes(64));

        var (status, body) = await TransitionAsync(Flow, id, "add-document",
            new
            {
                passport = new
                {
                    file = stored.GetProperty("file").GetString(), content = Convert.ToBase64String(Bytes(16)),
                }
            }, sync: true);

        AssertError(HttpStatusCode.BadRequest, "Instance:100048", status, body);
    }

    [Fact]
    public async Task Reference_OnStart_Is400()
    {
        var (_, foreign) = await StartWithPassportAsync(Bytes(64));

        var (status, body) = await StartAsync(new { passport = new { file = foreign.GetProperty("file").GetString() } },
            sync: true);

        AssertError(HttpStatusCode.BadRequest, "Instance:100048", status, body);
    }

    [Fact]
    public async Task InvalidBase64_Is400()
    {
        var (status, body) = await StartAsync(
            new { passport = new { name = "x.pdf", mimeType = Pdf, content = "not*base64!" } }, sync: true);

        AssertError(HttpStatusCode.BadRequest, "Instance:100048", status, body);
    }

    [Fact]
    public async Task MissingBinding_Start_Is503_AndCreatesNoInstance()
    {
        var key = $"fo-miss-{Guid.NewGuid():N}";
        var (status, body) = await SendRawJsonAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{MissingBinding}/instances/start?sync=true",
            JsonSerializer.Serialize(new { key, attributes = new { passport = Upload("a.pdf", Pdf, Bytes(32)) } }),
            Headers());

        AssertError(HttpStatusCode.ServiceUnavailable, "Instance:100047", status, body);

        var (lookup, lookupBody) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{MissingBinding}/instances?key={key}", headers: Headers());
        Assert.Equal(HttpStatusCode.OK, lookup);
        Assert.Equal(0, JsonDocument.Parse(lookupBody).RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ForwardedToActiveSubFlow_Sync_LeafSwapsAgainstItsOwnSchema()
    {
        var (parentId, childId) = await StartParentAsync();
        var bytes = Bytes(1024);

        var (status, body) = await TransitionAsync(Parent, parentId, "child-upload",
            new { passport = Upload("child.pdf", Pdf, bytes) }, sync: true);

        Assert.True(status == HttpStatusCode.OK, $"forwarded child-upload answered {(int)status}: {body}");
        Assert.Equal(parentId, Id(body));
        await AssertChildOwnsTheFileAsync(parentId, childId, bytes);
    }

    [Fact]
    public async Task ForwardedToActiveSubFlow_Async_Is202WithTheParentId_AndTheLeafOwnsTheFile()
    {
        var (parentId, childId) = await StartParentAsync();
        var bytes = Bytes(1024);

        var (status, body) = await TransitionAsync(Parent, parentId, "child-upload",
            new { passport = Upload("child.pdf", Pdf, bytes) }, sync: false);

        Assert.True(status == HttpStatusCode.Accepted, $"forwarded child-upload answered {(int)status}: {body}");
        Assert.Equal(parentId, Id(body));
        await WaitUntilAsync(async () => (await DataAsync(Child, childId)).TryGetProperty("passport", out _),
            $"child {childId} never received the passport");
        await AssertChildOwnsTheFileAsync(parentId, childId, bytes);
    }

    [Fact]
    public async Task TransitionMapping_RelocatesUploadToPassport_AndGetFileAsyncReadsTheBytes()
    {
        var id = await StartPlainAsync();
        var bytes = Bytes(8192);

        var (status, body) = await TransitionAsync(Flow, id, "to-review",
            new { upload = Upload("relocated.pdf", Pdf, bytes) }, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"to-review answered {(int)status}: {body}");
        await WaitForInstanceStateAsync(Flow, id, "review");
        await AssertNotFaultedAsync(Flow, id);

        var data = await DataAsync(Flow, id);
        var passport = data.GetProperty("passport");
        AssertHandle(passport, bytes, Flow, id, "relocated.pdf", Pdf);
        Assert.True(data.GetProperty("relocated").GetBoolean());
        Assert.False(data.TryGetProperty("upload", out _), "the unmapped upload reached the data");
        Assert.DoesNotContain("\"content\"", data.GetRawText(), StringComparison.Ordinal);

        var history = await HistoryRawAsync(Flow, id);
        Assert.Contains("to-review", history, StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", history, StringComparison.Ordinal);

        // review onEntry: GetFileAsync(domain, flow, instance, file) → SHA-256 of the bytes it read.
        Assert.Equal(passport.GetProperty("eTag").GetString(), data.GetProperty("checksum").GetString());
        Assert.Equal(bytes.Length, data.GetProperty("checksumSize").GetInt32());
    }

    // ── read path (functions/file) ──────────────────────────────────────────

    [Fact]
    public async Task FileFunction_ServesTheBytes_WithTheContractHeaders()
    {
        var bytes = Bytes(3000);
        var (id, handle) = await StartWithPassportAsync(bytes, TurkishName);

        using var response = await FileAsync(Flow, id, handle.GetProperty("file").GetString()!);
        var read = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Sha(bytes), Sha(read));
        Assert.Equal($"\"{handle.GetProperty("eTag").GetString()}\"", response.Headers.ETag?.Tag);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        var cache = response.Headers.CacheControl!;
        Assert.True(cache.Private);
        Assert.Equal(TimeSpan.FromSeconds(31536000), cache.MaxAge);
        Assert.Contains(cache.Extensions, e => e.Name == "immutable");
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("sandbox; default-src 'none'", Single(response, "Content-Security-Policy"));
        Assert.Equal(Pdf, response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("inline", disposition.DispositionType);
        Assert.Equal(TurkishName, disposition.FileNameStar);
        Assert.Contains("filename*=UTF-8''kimlik-%C3%B6n.pdf", disposition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileFunction_ConditionalRangeAndHead()
    {
        var bytes = Bytes(100);
        var (id, handle) = await StartWithPassportAsync(bytes);
        var file = handle.GetProperty("file").GetString()!;
        var etag = $"\"{handle.GetProperty("eTag").GetString()}\"";

        using (var notModified = await FileAsync(Flow, id, file, configure: r => r.Headers.TryAddWithoutValidation("If-None-Match", etag)))
        {
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
        }

        using (var partial = await FileAsync(Flow, id, file, configure: r => r.Headers.Range = new RangeHeaderValue(0, 1)))
        {
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal(bytes[..2], await partial.Content.ReadAsByteArrayAsync());
            Assert.Equal("bytes 0-1/100", partial.Content.Headers.ContentRange?.ToString());
        }

        using (var outOfRange = await FileAsync(Flow, id, file, configure: r => r.Headers.Range = new RangeHeaderValue(5000, 6000)))
        {
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, outOfRange.StatusCode);
        }

        using (var head = await FileAsync(Flow, id, file, HttpMethod.Head))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(etag, head.Headers.ETag?.Tag);
            Assert.Equal(Pdf, head.Content.Headers.ContentType?.MediaType);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task FileFunction_ActiveType_IsAnAttachment()
    {
        var bytes = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        var (status, body) = await StartAsync(new { files = new[] { Upload("page.html", "text/html", bytes) } }, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"start answered {(int)status}: {body}");
        var id = Id(body);
        var file = (await DataAsync(Flow, id)).GetProperty("files")[0].GetProperty("file").GetString()!;

        using var response = await FileAsync(Flow, id, file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("sandbox; default-src 'none'", Single(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task FileFunction_StateQueryRoles_DenyIs403()
    {
        var (id, handle) = await StartWithPassportAsync(Bytes(64));
        var file = handle.GetProperty("file").GetString()!;
        var (status, body) = await TransitionAsync(Flow, id, "lock", new { }, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"lock answered {(int)status}: {body}");

        using (var denied = await FileAsync(Flow, id, file))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Decided before the file is located: an unknown file id is 403 as well, not 404.
        using (var deniedUnknown = await FileAsync(Flow, id, Guid.NewGuid().ToString()))
            Assert.Equal(HttpStatusCode.Forbidden, deniedUnknown.StatusCode);

        using (var allowed = await FileAsync(Flow, id, file, roles: "fo.reader"))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task FileFunction_XRolesHiddenPath_Is404_AndVisibleWithTheRole()
    {
        var id = await StartPlainAsync();
        var (status, body) = await TransitionAsync(Flow, id, "add-document",
            new { secret = Upload("secret.pdf", Pdf, Bytes(64)) }, sync: true, roles: "fo.auditor");
        Assert.True(status == HttpStatusCode.OK, $"add-document answered {(int)status}: {body}");

        Assert.False((await DataAsync(Flow, id)).TryGetProperty("secret", out _), "x-roles did not prune secret");
        var secret = (await DataAsync(Flow, id, "fo.auditor")).GetProperty("secret");
        var file = secret.GetProperty("file").GetString()!;

        using (var hidden = await FileAsync(Flow, id, file))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        using (var visible = await FileAsync(Flow, id, file, roles: "fo.auditor"))
            Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
    }

    [Fact]
    public async Task FileFunction_AnotherInstancesFile_Is404()
    {
        var (_, foreign) = await StartWithPassportAsync(Bytes(64));
        var (id, _) = await StartWithPassportAsync(Bytes(64));

        using var response = await FileAsync(Flow, id, foreign.GetProperty("file").GetString()!);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Instance:100049", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplacingTheFile_OldIdIs404_NewIdIs200()
    {
        var (id, first) = await StartWithPassportAsync(Bytes(64));
        var replacement = Bytes(128);

        var (status, body) = await TransitionAsync(Flow, id, "add-document",
            new { passport = Upload("new.pdf", Pdf, replacement) }, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"replace answered {(int)status}: {body}");
        var second = (await DataAsync(Flow, id)).GetProperty("passport");
        Assert.NotEqual(first.GetProperty("file").GetString(), second.GetProperty("file").GetString());

        using (var old = await FileAsync(Flow, id, first.GetProperty("file").GetString()!))
            Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);

        var (newStatus, read) = await ReadFileBytesAsync(Flow, id, second.GetProperty("file").GetString()!);
        Assert.Equal(HttpStatusCode.OK, newStatus);
        Assert.Equal(Sha(replacement), Sha(read));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task AssertChildOwnsTheFileAsync(string parentId, string childId, byte[] bytes)
    {
        var childPassport = (await DataAsync(Child, childId)).GetProperty("passport");
        AssertHandle(childPassport, bytes, Child, childId, "child.pdf", Pdf);

        var parentData = (await DataAsync(Parent, parentId)).GetRawText();
        Assert.DoesNotContain("\"content\"", parentData, StringComparison.Ordinal);
        Assert.DoesNotContain(childPassport.GetProperty("file").GetString()!, parentData, StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", await HistoryRawAsync(Child, childId), StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", await HistoryRawAsync(Parent, parentId), StringComparison.Ordinal);

        var file = childPassport.GetProperty("file").GetString()!;
        using (var fromParent = await FileAsync(Parent, parentId, file))
            Assert.Equal(HttpStatusCode.NotFound, fromParent.StatusCode);

        var (status, read) = await ReadFileBytesAsync(Child, childId, file);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Sha(bytes), Sha(read));
    }

    private async Task<(string ParentId, string ChildId)> StartParentAsync()
    {
        var (status, body) = await StartAsync(new { note = "parent" }, sync: true, workflow: Parent);
        Assert.True(status == HttpStatusCode.OK, $"parent start answered {(int)status}: {body}");
        var parentId = Id(body);

        await WaitForObservedStateAsync(Parent, parentId, "c-waiting");
        var children = await GetActiveSubflowsAsync(Parent, parentId);
        Assert.True(children.Count == 1, $"expected one active subflow, got {children.Count}");
        var childId = children.Values.Single();
        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Child, childId)).Status == "A",
            $"child {childId} never rested");
        return (parentId, childId);
    }

    private async Task<string> StartPlainAsync()
    {
        var (status, body) = await StartAsync(new { note = "plain" }, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"start answered {(int)status}: {body}");
        return Id(body);
    }

    private async Task<(string Id, JsonElement Handle)> StartWithPassportAsync(byte[] bytes, string name = "doc.pdf")
    {
        var (status, body) = await StartAsync(new { passport = Upload(name, Pdf, bytes) }, sync: true);
        Assert.True(status == HttpStatusCode.OK, $"start answered {(int)status}: {body}");
        var id = Id(body);
        return (id, (await DataAsync(Flow, id)).GetProperty("passport"));
    }

    private Task<(HttpStatusCode Status, string Body)> StartAsync(object attributes, bool sync, string workflow = Flow) =>
        SendRawJsonAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{workflow}/instances/start?sync={(sync ? "true" : "false")}",
            JsonSerializer.Serialize(new { attributes }), Headers());

    private Task<(HttpStatusCode Status, string Body)> TransitionAsync(
        string workflow, string id, string transition, object attributes, bool sync, string? roles = null) =>
        SendRawJsonAsync(HttpMethod.Patch,
            $"api/v1/core/workflows/{workflow}/instances/{id}/transitions/{transition}?sync={(sync ? "true" : "false")}",
            JsonSerializer.Serialize(new { attributes }), Headers(roles));

    private async Task<JsonElement> DataAsync(string workflow, string id, string? roles = null)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{workflow}/instances/{id}/functions/data", headers: Headers(roles));
        Assert.True(status == HttpStatusCode.OK, $"data function answered {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("data").Clone();
    }

    private async Task<string> HistoryRawAsync(string workflow, string id)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{workflow}/instances/{id}/transitions", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"history answered {(int)status}: {raw}");
        return raw;
    }

    private async Task<HttpResponseMessage> FileAsync(
        string workflow, string id, string file, HttpMethod? method = null, string? roles = null,
        Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get,
            $"api/v1/core/workflows/{workflow}/instances/{id}/functions/file?file={file}");
        foreach (var (key, value) in Headers(roles)) request.Headers.TryAddWithoutValidation(key, value);
        configure?.Invoke(request);
        return await _http.SendAsync(request);
    }

    private async Task<(HttpStatusCode Status, byte[] Bytes)> ReadFileBytesAsync(string workflow, string id, string file)
    {
        using var response = await FileAsync(workflow, id, file);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
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
        JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()
        ?? throw new InvalidOperationException($"no id in {body}");

    private static string Single(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) ? string.Join(",", values) : "";

    private static void AssertError(HttpStatusCode expected, string code, HttpStatusCode status, string body)
    {
        Assert.True(status == expected, $"expected {(int)expected} {code}, got {(int)status}: {body}");
        Assert.Contains(code, body, StringComparison.Ordinal);
    }

    private static void AssertHandle(JsonElement handle, byte[] bytes, string flow, string instance, string name, string mimeType)
    {
        Assert.False(handle.TryGetProperty("content", out _), $"content kept: {handle}");
        Assert.Equal(Binding, handle.GetProperty("component").GetString());
        Assert.True(Guid.TryParseExact(handle.GetProperty("file").GetString(), "D", out _), $"file is not a D-GUID: {handle}");
        Assert.Equal(bytes.Length, handle.GetProperty("size").GetInt64());
        Assert.Equal(Sha(bytes), handle.GetProperty("eTag").GetString());
        Assert.Equal(name, handle.GetProperty("name").GetString());
        Assert.Equal(mimeType, handle.GetProperty("mimeType").GetString());
        var owner = handle.GetProperty("owner");
        Assert.Equal("core", owner.GetProperty("domain").GetString());
        Assert.Equal(flow, owner.GetProperty("flow").GetString());
        Assert.Equal(instance, owner.GetProperty("instance").GetString());
    }

    private static void AssertSameHandle(JsonElement expected, JsonElement actual)
    {
        foreach (var member in new[] { "component", "file", "name", "mimeType", "eTag" })
            Assert.Equal(expected.GetProperty(member).GetString(), actual.GetProperty(member).GetString());
        Assert.Equal(expected.GetProperty("size").GetInt64(), actual.GetProperty("size").GetInt64());
        Assert.Equal(expected.GetProperty("owner").GetRawText(), actual.GetProperty("owner").GetRawText());
    }
}
