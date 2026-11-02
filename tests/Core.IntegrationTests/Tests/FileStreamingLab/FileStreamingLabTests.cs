using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.FileStreamingLab;

/// <summary>
/// file-streaming-lab (vnext-client-sdk-core#137, #142, #145): x-storage on real object stores, every one reached through
/// a Dapr bindings.aws.s3 component (the runtime carries no store SDK) — MinIO, and a DUMB S3 behind TLS that keeps
/// request bodies as received (the bank's store in #145), written by the runtime's own sidecar (1.16.x locally) and, for
/// the bank's write path, by the lab's app-less daprd 1.18.2 (fsl-dapr118).
/// <para>Requires <c>labs/file-streaming/lab.sh up</c> and the host env from <c>lab.sh env</c>; skips otherwise.</para>
/// </summary>
public class FileStreamingLabTests : WorkflowTestBase
{
    private const string Flow = "fsl-flow";
    private const string Pdf = "application/pdf";
    private const string StubAdmin = "http://localhost:9100/_admin/objects";
    private const string Dapr118 = "http://localhost:3599/v1.0/bindings/fsl-stub-dapr";

    private static readonly Lazy<string?> LabMissing = new(() =>
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var stub = client.GetAsync($"{StubAdmin}/probe/probe").GetAwaiter().GetResult();     // 404 = alive
            using var minio = client.GetAsync("http://localhost:9000/minio/health/live").GetAwaiter().GetResult();
            return minio.IsSuccessStatusCode ? null : "MinIO :9000 is not live";
        }
        catch (Exception ex) { return $"file-streaming lab not reachable ({ex.GetType().Name}); run labs/file-streaming/lab.sh up"; }
    });

    private readonly HttpClient _http;
    private static readonly HttpClient Lab = new() { Timeout = TimeSpan.FromSeconds(60) };

    public FileStreamingLabTests(VNextTestEnvironment environment) : base(environment)
    {
        _http = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(3) };
    }

    private static void RequireLab() => Skip.If(LabMissing.Value is not null, LabMissing.Value);

    // ── #137: client members beside the file survive the swap ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Inline_FilePickerRow_KeepsDocumentType_AndTheBytesLiveInMinio()
    {
        RequireLab();
        var bytes = Bytes(4096);
        var id = await StartOkAsync(new { files = new[] { Upload("invoice.pdf", Pdf, bytes, documentType: "invoice") } });

        var row = (await DataAsync(id)).GetProperty("files")[0];
        Assert.Equal("invoice", row.GetProperty("documentType").GetString());
        AssertHandle(row, bytes, "fsl-minio", id);
        Assert.Equal(Sha(bytes), Sha(await ReadFileAsync(id, row.GetProperty("file").GetString()!)));

        // Echo with a changed documentType: the stored handle wins its members, the client's own member is kept.
        var (status, body) = await TransitionAsync(id, "add-document",
            new { files = new[] { new { file = row.GetProperty("file").GetString(), size = 1, documentType = "receipt" } } });
        Assert.True(status == HttpStatusCode.OK, $"echo answered {(int)status}: {body}");
        var echoed = (await DataAsync(id)).GetProperty("files")[0];
        Assert.Equal("receipt", echoed.GetProperty("documentType").GetString());
        Assert.Equal(bytes.Length, echoed.GetProperty("size").GetInt64());
    }

    // ── streaming (definition-based) ─────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task StreamedUpload_LargerThanTheJsonBodyLimit_IsClaimed_AndServedWithRanges()
    {
        RequireLab();
        var bytes = Bytes(30 * 1024 * 1024);     // 30 MiB: three times the 10 MiB JSON body limit
        var ticket = await UploadAsync("files[]", "scan.bin", "application/octet-stream", bytes);
        Assert.Equal("fsl-minio", ticket.GetProperty("component").GetString());
        Assert.Equal(bytes.Length, ticket.GetProperty("size").GetInt64());
        Assert.Equal(Sha(bytes), ticket.GetProperty("eTag").GetString());

        var file = ticket.GetProperty("file").GetString()!;
        var id = await StartOkAsync(new { files = new object[] { new { file, documentType = "scan" } } });

        var row = (await DataAsync(id)).GetProperty("files")[0];
        Assert.Equal("scan", row.GetProperty("documentType").GetString());
        AssertHandle(row, bytes, "fsl-minio", id, mimeType: "application/octet-stream", name: "scan.bin");
        Assert.Equal(Sha(bytes), Sha(await ReadFileAsync(id, file)));

        using var range = await FileAsync(id, file, r => r.Headers.Range = new RangeHeaderValue(10_000_000, 10_000_999));
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(bytes[10_000_000..10_001_000], await range.Content.ReadAsByteArrayAsync());

        using var head = await FileAsync(id, file, method: HttpMethod.Head);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
    }

    [SkippableFact]
    public async Task StreamedUpload_IsSingleUse_AndBoundToItsPath()
    {
        RequireLab();
        var ticket = await UploadAsync("files[]", "a.bin", "application/octet-stream", Bytes(1000));
        var file = ticket.GetProperty("file").GetString()!;
        await StartOkAsync(new { files = new object[] { new { file } } });

        var (status, body) = await StartAsync(new { files = new object[] { new { file } } });
        AssertError(HttpStatusCode.BadRequest, "Instance:100048", status, body);

        var other = (await UploadAsync("streamDoc", "b.bin", "application/octet-stream", Bytes(10))).GetProperty("file").GetString();
        (status, body) = await StartAsync(new { files = new object[] { new { file = other } } });
        AssertError(HttpStatusCode.BadRequest, "Instance:100048", status, body);
    }

    [SkippableFact]
    public async Task Transport_DecidesTheChannel_PerField()
    {
        RequireLab();
        // stream-only field: inline content is refused
        var (status, body) = await StartAsync(new { streamDoc = Upload("x.pdf", Pdf, Bytes(10)) });
        AssertError(HttpStatusCode.BadRequest, "Instance:100048", status, body);
        Assert.Contains("streamed uploads only", body, StringComparison.Ordinal);

        // inline-only field: the upload endpoint refuses it; an unknown path too
        (status, body) = await UploadRawAsync("inlineDoc", "x.pdf", Pdf, Bytes(10));
        AssertError(HttpStatusCode.BadRequest, "Instance:100048", status, body);
        (status, body) = await UploadRawAsync("nope", "x.pdf", Pdf, Bytes(10));
        AssertError(HttpStatusCode.BadRequest, "Instance:100048", status, body);

        // stream-only field: a streamed upload is claimed
        var ticket = await UploadAsync("streamDoc", "s.pdf", Pdf, Bytes(2048));
        var id = await StartOkAsync(new { streamDoc = new { file = ticket.GetProperty("file").GetString() } });
        Assert.Equal(ticket.GetProperty("eTag").GetString(), (await DataAsync(id)).GetProperty("streamDoc").GetProperty("eTag").GetString());
    }

    // ── #145: the bank's dumb S3 behind TLS ──────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RuntimeSidecarOverTls_ToAStoreThatDoesNotDecodeAwsChunked_ReadsBackTheOriginal()
    {
        RequireLab();
        var bytes = Pdf6265();
        var id = await StartOkAsync(new { bankDoc = Upload("contract.pdf", Pdf, bytes) });
        var handle = (await DataAsync(id)).GetProperty("bankDoc");
        AssertHandle(handle, bytes, "fsl-stub", id);

        // What lands in the store is the sidecar's business: daprd 1.16.x sends the file as is, 1.17+ frames it
        // (aws-chunked + CRC32 trailer). Either way it is this file, and the runtime serves the original.
        var stored = await StubObjectAsync("fsl-stub", handle.GetProperty("file").GetString()!);
        var framed = Header(stored.GetProperty("headers"), "Content-Encoding")?.Contains("aws-chunked") == true;
        if (framed)
            Assert.True(stored.GetProperty("size").GetInt32() > bytes.Length, $"aws-chunked but not longer: {stored}");
        else
            Assert.Equal(Sha(bytes), stored.GetProperty("sha256").GetString());

        Assert.Equal(Sha(bytes), Sha(await ReadFileAsync(id, handle.GetProperty("file").GetString()!)));
    }

    [SkippableFact]
    public async Task ObjectFramedByDapr118_IsServedAsTheOriginal()
    {
        RequireLab();
        var bytes = Pdf6265();
        var id = await StartOkAsync(new { bankDapr = Upload("contract.pdf", Pdf, bytes) });
        var handle = (await DataAsync(id)).GetProperty("bankDapr");
        var file = handle.GetProperty("file").GetString()!;

        // The bank's write path: daprd 1.18 bindings.aws.s3 over TLS frames the upload; the dumb store keeps the frame.
        await Dapr118CreateAsync(file, bytes);
        var stored = await StubObjectAsync("fsl-stub-dapr", file);
        Assert.Equal("aws-chunked", Header(stored.GetProperty("headers"), "Content-Encoding"));
        Assert.True(stored.GetProperty("size").GetInt32() > bytes.Length, $"not framed: {stored}");

        // functions/file and GetFileAsync both read through InstanceFileAppService: the original, never the frame.
        var served = await ReadFileAsync(id, file);
        Assert.Equal(bytes.Length, served.Length);
        Assert.Equal(Sha(bytes), Sha(served));
    }

    [SkippableFact]
    public async Task ObjectThatNoLongerMatchesItsHandle_Is503_NotServed()
    {
        RequireLab();
        var bytes = Bytes(6000);
        var id = await StartOkAsync(new { bankDoc = Upload("a.pdf", Pdf, bytes) });
        var file = (await DataAsync(id)).GetProperty("bankDoc").GetProperty("file").GetString()!;

        using (var corrupt = new ByteArrayContent(Bytes(4000)))
            (await Lab.PutAsync($"{StubAdmin}/fsl-stub/{file}", corrupt)).EnsureSuccessStatusCode();

        using var response = await FileAsync(id, file);
        var body = await response.Content.ReadAsStringAsync();
        AssertError(HttpStatusCode.ServiceUnavailable, "Instance:100047", response.StatusCode, body);
    }

    // ── #142-A: process a file in a script and write it back ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Script_ReadsAFile_ProcessesItWithStreams_AndWritesItBack()
    {
        RequireLab();
        var bytes = Bytes(5000);
        var id = await StartOkAsync(new { files = new[] { Upload("in.bin", "application/octet-stream", bytes) } });
        var (status, body) = await TransitionAsync(id, "convert", new { });
        Assert.True(status == HttpStatusCode.OK, $"convert answered {(int)status}: {body}");

        var data = await DataAsync(id);
        Assert.False(data.TryGetProperty("convertError", out var error), $"convert failed: {error}");
        var converted = data.GetProperty("converted");
        var expected = Encoding.ASCII.GetBytes("FSL-CONVERTED\n").Concat(bytes.Reverse()).ToArray();
        AssertHandle(converted, expected, "fsl-minio", id, mimeType: "application/octet-stream", name: "converted.bin");
        Assert.Equal(Sha(bytes), data.GetProperty("convertedFrom").GetString());
        Assert.Equal(expected, await ReadFileAsync(id, converted.GetProperty("file").GetString()!));
    }

    // ── #142-B/D: list, get and delete through the DaprBinding task ──────────────────────────────────────────────

    [SkippableFact]
    public async Task BindingTask_ListsGetsAndDeletes_TheInstancesObject()
    {
        RequireLab();
        var bytes = Bytes(3000);
        var id = await StartOkAsync(new { files = new[] { Upload("doc.bin", "application/octet-stream", bytes) } });
        var file = (await DataAsync(id)).GetProperty("files")[0].GetProperty("file").GetString()!;

        var (status, body) = await TransitionAsync(id, "inspect", new { });
        Assert.True(status == HttpStatusCode.OK, $"inspect answered {(int)status}: {body}");
        var data = await DataAsync(id);
        var listing = data.GetProperty("listing");
        Assert.True(listing.GetProperty("success").GetBoolean(), $"list failed: {listing}");
        Assert.Equal("json", listing.GetProperty("encoding").GetString());
        Assert.Contains(file, listing.GetProperty("text").GetString(), StringComparison.Ordinal);
        var got = data.GetProperty("got");
        Assert.Equal("base64", got.GetProperty("encoding").GetString());
        Assert.Equal(bytes.Length, got.GetProperty("length").GetInt32());
        Assert.Equal(Convert.ToBase64String(bytes)[..16], got.GetProperty("base64Prefix").GetString());

        (status, body) = await TransitionAsync(id, "back", new { });
        Assert.True(status == HttpStatusCode.OK, $"back answered {(int)status}: {body}");
        (status, body) = await TransitionAsync(id, "delete-file", new { });
        Assert.True(status == HttpStatusCode.OK, $"delete-file answered {(int)status}: {body}");
        Assert.True((await DataAsync(id)).GetProperty("deleted").GetProperty("success").GetBoolean());

        // The handle still names the deleted object: the binding's get fails ("object not found"), which the runtime
        // cannot tell from an outage, so the read is 503 (docs: clear the field after a delete).
        using var gone = await FileAsync(id, file);
        AssertError(HttpStatusCode.ServiceUnavailable, "Instance:100047", gone.StatusCode, await gone.Content.ReadAsStringAsync());
    }

    // ── #142-E / #99: a raw function serves a PDF as bytes ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RawFunction_ServesTheStoredPdf_AsItsBytes()
    {
        RequireLab();
        var bytes = Pdf6265();
        var id = await StartOkAsync(new { inlineDoc = Upload("contract.pdf", Pdf, bytes) });

        var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/core/workflows/{Flow}/instances/{id}/functions/fsl-download");
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await _http.SendAsync(request);
        var served = await response.Content.ReadAsByteArrayAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"fsl-download answered {(int)response.StatusCode}: {Encoding.UTF8.GetString(served)}");
        Assert.Equal(Pdf, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Sha(bytes), Sha(served));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<string> StartOkAsync(object attributes)
    {
        var (status, body) = await StartAsync(attributes);
        Assert.True(status == HttpStatusCode.OK, $"start answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    private Task<(HttpStatusCode Status, string Body)> StartAsync(object attributes) =>
        SendRawJsonAsync(HttpMethod.Post, $"api/v1/core/workflows/{Flow}/instances/start?sync=true",
            JsonSerializer.Serialize(new { attributes }), Headers());

    private Task<(HttpStatusCode Status, string Body)> TransitionAsync(string id, string transition, object attributes) =>
        SendRawJsonAsync(HttpMethod.Patch, $"api/v1/core/workflows/{Flow}/instances/{id}/transitions/{transition}?sync=true",
            JsonSerializer.Serialize(new { attributes }), Headers());

    private async Task<JsonElement> DataAsync(string id)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get, $"api/v1/core/workflows/{Flow}/instances/{id}/functions/data", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"data function answered {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("data").Clone();
    }

    private async Task<(HttpStatusCode Status, string Body)> UploadRawAsync(string path, string name, string mimeType, byte[] bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/v1/core/workflows/{Flow}/files?path={Uri.EscapeDataString(path)}&name={Uri.EscapeDataString(name)}")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await _http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> UploadAsync(string path, string name, string mimeType, byte[] bytes)
    {
        var (status, body) = await UploadRawAsync(path, name, mimeType, bytes);
        Assert.True(status == HttpStatusCode.Created, $"upload to {path} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> FileAsync(string id, string file, Action<HttpRequestMessage>? configure = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"api/v1/core/workflows/{Flow}/instances/{id}/functions/file?file={file}");
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);
        configure?.Invoke(request);
        return await _http.SendAsync(request);
    }

    private async Task<byte[]> ReadFileAsync(string id, string file)
    {
        using var response = await FileAsync(id, file);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"functions/file answered {(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");
        return bytes;
    }

    private static async Task<JsonElement> StubObjectAsync(string bucket, string key)
    {
        var raw = await Lab.GetStringAsync($"{StubAdmin}/{bucket}/{key}");
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static async Task Dapr118CreateAsync(string key, byte[] bytes)
    {
        var payload = JsonSerializer.Serialize(new { operation = "create", data = Convert.ToBase64String(bytes), metadata = new { key } });
        using var response = await Lab.PostAsync(Dapr118, new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, $"daprd 1.18 create answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static string? Header(JsonElement headers, string name) =>
        headers.EnumerateObject().FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v
            ? v.GetString() : null;

    private static object Upload(string name, string mimeType, byte[] bytes, string? documentType = null) =>
        documentType is null
            ? new { name, mimeType, size = bytes.Length, content = Convert.ToBase64String(bytes) }
            : new { name, mimeType, size = bytes.Length, content = Convert.ToBase64String(bytes), documentType };

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>A 6265-byte "PDF" — the size the bank measured (#145).</summary>
    private static byte[] Pdf6265()
    {
        var bytes = Bytes(6265);
        "%PDF-1.4\n"u8.CopyTo(bytes);
        "\n%%EOF\n"u8.CopyTo(bytes.AsSpan(6265 - 7));
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertError(HttpStatusCode expected, string code, HttpStatusCode status, string body)
    {
        Assert.True(status == expected, $"expected {(int)expected} {code}, got {(int)status}: {body}");
        Assert.Contains(code, body, StringComparison.Ordinal);
    }

    private static void AssertHandle(JsonElement handle, byte[] bytes, string component, string instance,
        string? mimeType = null, string? name = null)
    {
        Assert.False(handle.TryGetProperty("content", out _), $"content kept: {handle}");
        Assert.Equal(component, handle.GetProperty("component").GetString());
        Assert.True(Guid.TryParseExact(handle.GetProperty("file").GetString(), "D", out _), $"file is not a D-GUID: {handle}");
        Assert.Equal(bytes.Length, handle.GetProperty("size").GetInt64());
        Assert.Equal(Sha(bytes), handle.GetProperty("eTag").GetString());
        if (mimeType is not null) Assert.Equal(mimeType, handle.GetProperty("mimeType").GetString());
        if (name is not null) Assert.Equal(name, handle.GetProperty("name").GetString());
        var owner = handle.GetProperty("owner");
        Assert.Equal("core", owner.GetProperty("domain").GetString());
        Assert.Equal(Flow, owner.GetProperty("flow").GetString());
        Assert.Equal(instance, owner.GetProperty("instance").GetString());
    }
}
