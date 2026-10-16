using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ForgeFileFormLab;

/// <summary>
/// forge-file-form-lab: the shape vNext Forge's Quick Run file widget relies on. Unlike file-offload-lab, the start
/// and the <c>replace</c> transition carry a <c>transition</c>-role schema (<c>ffl-upload</c>) that describes the
/// upload (<c>name</c>, <c>mimeType</c>, <c>size</c>, <c>content</c>), while the workflow's master schema
/// (<c>ffl-master</c>) marks the same paths with <c>x-storage</c> — one field and one single-level array. Forge finds
/// the upload fields through the master paths and renders a file picker for them.
/// <para>
/// Pins: the runtime-served transition schema keeps the upload shape (what Forge renders), start and transition
/// uploads become handles, omitting a file field keeps the stored file, a new upload replaces it, and
/// <c>functions/file</c> serves every stored file by its handle's owner.
/// </para>
/// </summary>
public class ForgeFileFormLabTests : WorkflowTestBase
{
    private const string Flow = "ffl-flow";
    private const string Binding = "vnext-blob-local";
    private const string Pdf = "application/pdf";

    private readonly HttpClient _http;

    public ForgeFileFormLabTests(VNextTestEnvironment environment) : base(environment)
    {
        _http = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    [Fact]
    public async Task SchemaFunction_ServesTheUploadShape_ForTheForm()
    {
        var id = await StartAsync(new { note = "schema" });

        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Flow}/instances/{id}/functions/schema?transitionKey=replace", headers: Headers());

        Assert.True(status == HttpStatusCode.OK, $"schema function answered {(int)status}: {raw}");
        Assert.Contains("\"content\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("x-storage", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_WithFieldAndArrayUploads_StoresHandles()
    {
        var document = Bytes(1500);
        var first = Bytes(700);
        var second = Bytes(900);

        var id = await StartAsync(new
        {
            note = "start",
            document = Upload("id-card.pdf", Pdf, document),
            attachments = new[] { Upload("a.png", "image/png", first), Upload("b.txt", "text/plain", second) },
        });

        var data = await DataAsync(id);
        AssertHandle(data.GetProperty("document"), document, id, "id-card.pdf", Pdf);
        var attachments = data.GetProperty("attachments");
        Assert.Equal(2, attachments.GetArrayLength());
        AssertHandle(attachments[0], first, id, "a.png", "image/png");
        AssertHandle(attachments[1], second, id, "b.txt", "text/plain");
        Assert.DoesNotContain("\"content\"", data.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(second, await FileBytesAsync(id, attachments[1].GetProperty("file").GetString()!));
    }

    [Fact]
    public async Task Replace_WithoutTheField_KeepsTheStoredFile()
    {
        var bytes = Bytes(1024);
        var id = await StartAsync(new { document = Upload("keep.pdf", Pdf, bytes) });
        var stored = (await DataAsync(id)).GetProperty("document").GetProperty("file").GetString();

        await TransitionAsync(id, "replace", new { note = "no file in this request" });

        var data = await DataAsync(id);
        Assert.Equal(stored, data.GetProperty("document").GetProperty("file").GetString());
        Assert.Equal("no file in this request", data.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Replace_WithANewUpload_ReplacesTheHandle_AndTheOldIdIs404()
    {
        var id = await StartAsync(new { document = Upload("v1.pdf", Pdf, Bytes(512)) });
        var oldId = (await DataAsync(id)).GetProperty("document").GetProperty("file").GetString()!;

        var replacement = Bytes(2048);
        await TransitionAsync(id, "replace", new { document = Upload("v2.pdf", Pdf, replacement) });

        var handle = (await DataAsync(id)).GetProperty("document");
        AssertHandle(handle, replacement, id, "v2.pdf", Pdf);
        Assert.NotEqual(oldId, handle.GetProperty("file").GetString());
        Assert.Equal(replacement, await FileBytesAsync(id, handle.GetProperty("file").GetString()!));

        using var old = await _http.GetAsync($"api/v1/core/workflows/{Flow}/instances/{id}/functions/file?file={oldId}");
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<string> StartAsync(object attributes)
    {
        var (status, body) = await SendRawJsonAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{Flow}/instances/start?sync=true",
            JsonSerializer.Serialize(new { attributes }), Headers());
        Assert.True(status == HttpStatusCode.OK, $"start answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"no id in {body}");
    }

    private async Task TransitionAsync(string id, string transition, object attributes)
    {
        var (status, body) = await SendRawJsonAsync(HttpMethod.Patch,
            $"api/v1/core/workflows/{Flow}/instances/{id}/transitions/{transition}?sync=true",
            JsonSerializer.Serialize(new { attributes }), Headers());
        Assert.True(status == HttpStatusCode.OK, $"{transition} answered {(int)status}: {body}");
    }

    private async Task<JsonElement> DataAsync(string id)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Flow}/instances/{id}/functions/data", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"data function answered {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("data").Clone();
    }

    private async Task<byte[]> FileBytesAsync(string id, string file)
    {
        using var response = await _http.GetAsync($"api/v1/core/workflows/{Flow}/instances/{id}/functions/file?file={file}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"file function answered {(int)response.StatusCode}");
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static object Upload(string name, string mimeType, byte[] bytes) =>
        new { name, mimeType, size = bytes.Length, content = Convert.ToBase64String(bytes) };

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static void AssertHandle(JsonElement handle, byte[] bytes, string instance, string name, string mimeType)
    {
        Assert.False(handle.TryGetProperty("content", out _), $"content kept: {handle}");
        Assert.Equal(Binding, handle.GetProperty("component").GetString());
        Assert.Equal(bytes.Length, handle.GetProperty("size").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), handle.GetProperty("eTag").GetString());
        Assert.Equal(name, handle.GetProperty("name").GetString());
        Assert.Equal(mimeType, handle.GetProperty("mimeType").GetString());
        var owner = handle.GetProperty("owner");
        Assert.Equal(Flow, owner.GetProperty("flow").GetString());
        Assert.Equal(instance, owner.GetProperty("instance").GetString());
    }
}
