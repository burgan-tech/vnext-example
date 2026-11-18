using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.VersionPinLab;

/// <summary>
/// version-pin-lab, package versions: the shape a built domain package publishes —
/// <c>ARTIFACT-pkg.PACKAGE+domain</c> — resolved from every request form a consumer may write.
/// <list type="bullet">
///   <item><c>1.0.0</c> (artifact) → the highest PACKAGE of that artifact, compared numerically
///   (<c>1.0.123</c> beats <c>1.0.99</c> and <c>1.0.9</c>), whatever order they were published in.</item>
///   <item><c>1.0</c> / <c>1</c> → the highest artifact in range, then its highest package.</item>
///   <item><c>1.0.0-pkg.1.0.99</c> (full, without <c>+core</c>) → the stored <c>…+core</c> row: build
///   metadata takes no part in a version's identity.</item>
/// </list>
/// <para>
/// Regression guard for vnext's floating-version fix: these answers used to be computed from a list
/// holding every published body, which on a long publish history ran out of memory, dropped the
/// newest rows and resolved an older version. The runtime now decides from the version strings and
/// loads one body; the answers must not change.
/// </para>
/// <para>
/// The versions are published here, built from the 1.0.0 child file under a key of their own, so
/// the bare <c>1.0.0</c>/<c>1.0.3</c> rows of <see cref="VersionPinLabTests"/> never mix in. The
/// highest package is deliberately NOT the last one published, so a publish-time cache warm-up
/// that assumed "newest publish wins" would answer wrong. Publish is version-immutable: an
/// "already published" answer on a re-run is fine.
/// </para>
/// </summary>
public class PackageVersionResolutionTests : WorkflowTestBase
{
    private const string Child = "version-pin-lab-pkg-child";

    /// <summary>Publish order: the highest of 1.0.0 (pkg 1.0.123) goes out before a lower one.</summary>
    private static readonly string[] PublishOrder =
    [
        "1.0.0-pkg.1.0.9+core",
        "1.0.0-pkg.1.0.123+core",
        "1.1.0-pkg.1.0.1+core",
        "1.0.0-pkg.1.0.99+core"
    ];

    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private static bool _published;

    public PackageVersionResolutionTests(VNextTestEnvironment environment) : base(environment) { }

    [Theory]
    [InlineData("1.0.0", "1.0.0-pkg.1.0.123+core")]
    [InlineData("1.0", "1.0.0-pkg.1.0.123+core")]
    [InlineData("1", "1.1.0-pkg.1.0.1+core")]
    [InlineData("latest", "1.1.0-pkg.1.0.1+core")]
    public async Task FloatingRequest_ResolvesTheHighestPackage(string requested, string expected)
    {
        await EnsurePublishedAsync();

        Assert.Equal(expected, await StartedVersionAsync(requested));
    }

    [Theory]
    [InlineData("1.0.0-pkg.1.0.99", "1.0.0-pkg.1.0.99+core")]
    [InlineData("1.0.0-pkg.1.0.99+core", "1.0.0-pkg.1.0.99+core")]
    public async Task FullVersion_ResolvesThatPackage_WithOrWithoutBuildMetadata(string requested, string expected)
    {
        await EnsurePublishedAsync();

        Assert.Equal(expected, await StartedVersionAsync(requested));
    }

    [Fact]
    public async Task FullVersionNotPublished_IsRefused()
    {
        await EnsurePublishedAsync();

        var (status, body) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{Child}/instances/start?version=1.0.0-pkg.1.0.124&sync=true", new { }, Headers());

        Assert.True((int)status >= 400, $"an unpublished full version was started: {body}");
    }

    /// <summary>
    /// The read a domain team uses instead of the database (vnext#1192): every published build, newest publish
    /// first, without bodies, and what a versionless start resolves to — which must be the build new instances
    /// actually start on, and the one the cached answer gives.
    /// </summary>
    [Fact]
    public async Task VersionsView_ListsEveryBuild_AndTheBuildNewInstancesStartOn()
    {
        await EnsurePublishedAsync();

        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/components/workflows/{Child}/versions?pageSize=100", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"versions answered {(int)status}: {body}");
        var view = JsonDocument.Parse(body).RootElement;

        var items = view.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(PublishOrder.Length, view.GetProperty("totalVersions").GetInt32());
        Assert.Equal(PublishOrder.OrderBy(v => v, StringComparer.Ordinal),
            items.Select(i => i.GetProperty("version").GetString()).OrderBy(v => v, StringComparer.Ordinal));
        var publishedAt = items.Select(i => i.GetProperty("publishedAt").GetDateTimeOffset()).ToList();
        Assert.Equal(publishedAt.OrderByDescending(t => t), publishedAt);
        Assert.False(view.TryGetProperty("defaultVersion", out _), "this flow has no default-version pointer");

        var latest = view.GetProperty("latest");
        Assert.Equal("resolved", latest.GetProperty("outcome").GetString());
        Assert.Equal("automatic", latest.GetProperty("source").GetString());
        Assert.Equal("1.1.0-pkg.1.0.1+core", latest.GetProperty("version").GetString());

        Assert.Equal("1.1.0-pkg.1.0.1+core", await StartedVersionAsync("latest"));
        Assert.Equal("1.1.0-pkg.1.0.1+core", await CachedVersionAsync(null));
    }

    /// <summary>
    /// <c>utilities/invalidate</c> bumps the flow's cache generation, so every cached answer becomes unreachable
    /// at once — what a cache entry's expiry does, which is when vnext#1192 started old builds. Its re-cast
    /// re-warms the common spellings (<c>latest</c>, <c>1.0.0</c>, <c>1.0</c>, <c>1</c> of the re-cast version) from
    /// the stored versions; <c>1.1</c> is not among them, so it is a true cold miss resolved from the database.
    /// None of the answers may move.
    /// </summary>
    [Fact]
    public async Task AfterTheCachedAnswersAreDropped_NewInstancesStillStartOnTheHighestBuild()
    {
        await EnsurePublishedAsync();

        var (status, body) = await SendRawAsync(HttpMethod.Post, "api/v1/utilities/invalidate",
            new { domain = "core", flow = "sys-flows", key = Child, version = "1.0.0-pkg.1.0.9+core" }, Headers());
        Assert.True((int)status < 300, $"invalidate answered {(int)status}: {body}");
        await Task.Delay(TimeSpan.FromSeconds(6)); // the per-pod generation memo, ComponentCache:GenerationMemoSeconds = 5

        Assert.Equal("1.0.0-pkg.1.0.123+core", await StartedVersionAsync("1.0.0"));
        Assert.Equal("1.1.0-pkg.1.0.1+core", await StartedVersionAsync("latest"));
        Assert.Equal("1.0.0-pkg.1.0.123+core", await CachedVersionAsync("1.0.0"));
        Assert.Equal("1.1.0-pkg.1.0.1+core", await StartedVersionAsync("1.1")); // not warmed: a cold resolution
    }

    [Fact]
    public async Task VersionsView_ForAnUnpublishedFlow_IsNotFound()
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            "api/v1/core/components/workflows/version-pin-lab-never-published/versions", headers: Headers());

        Assert.True(status == HttpStatusCode.NotFound, $"expected 404, got {(int)status}: {body}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The version the component cache answers for a request, through the discovery read.</summary>
    private async Task<string?> CachedVersionAsync(string? requested)
    {
        var url = $"api/v1/core/components/workflows/{Child}" + (requested is null ? "" : "/" + Uri.EscapeDataString(requested));
        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"GET {url} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("version").GetString();
    }

    private async Task<string?> StartedVersionAsync(string requested)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{Child}/instances/start?version={Uri.EscapeDataString(requested)}&sync=true",
            new { }, Headers());

        Assert.True((int)status < 400, $"start ?version={requested} was refused with {(int)status}: {body}");

        var root = JsonDocument.Parse(body).RootElement;
        if (root.TryGetProperty("flowVersion", out var top)) return top.GetString();
        if (root.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("flowVersion", out var inner))
            return inner.GetString();
        Assert.Fail($"start response carried no flowVersion: {body}");
        return null;
    }

    private async Task EnsurePublishedAsync()
    {
        await PublishGate.WaitAsync();
        try
        {
            if (_published) return;

            var path = Path.Combine(RepoRoot(), "core", "Workflows", "version-pin-lab", "version-pin-lab-child.json");
            var template = File.ReadAllText(path);

            foreach (var version in PublishOrder)
            {
                var definition = JsonNode.Parse(template)!.AsObject();
                definition["key"] = Child;
                definition["version"] = version;
                definition["tags"]!.AsArray().Add("package-version");
                definition["attributes"]!["labels"]!.AsArray()[0]!["label"] = $"Version Pin Lab Package Child ({version})";

                var (status, body) = await SendRawJsonAsync(
                    HttpMethod.Post, "api/v1/definitions/publish", definition.ToJsonString(), Headers());

                var alreadyThere = status == HttpStatusCode.Conflict
                                   || body.Contains("already", StringComparison.OrdinalIgnoreCase);
                Assert.True((int)status < 300 || alreadyThere, $"publishing {Child}@{version} answered {(int)status}: {body}");
            }

            _published = true;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "vnext.config.json"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("vnext.config.json not found above " + AppContext.BaseDirectory);
    }
}
