using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.FlowDefaultVersion;

/// <summary>
/// flow-default-version: the <c>sys-version-registry</c> pointer (instance key = flow key, data
/// <c>{ type: "Flow", defaultVersion }</c>) decides which version a versionless / <c>latest</c> /
/// partial start lands on. No registry entry = latest (unchanged behaviour). Running instances
/// never move.
/// <para>
/// The registry flow is not on the runtime yet (the core system package comes from npm), so a
/// verbatim copy lives under <c>fixtures/</c> and is published once from here, schema first.
/// Every test owns its fixture flow key (<c>default-version-lab-a..e</c>) so tests never fight over
/// one registry entry. Versions 1.0.1 / 1.1.0 (and 1.0.2 in the line test) are built from the
/// 1.0.0 file under <c>core/</c> and published through <c>api/v1/definitions/publish</c>;
/// publish is version-immutable, "already published" is fine on a re-run.
/// </para>
/// </summary>
public class FlowDefaultVersionTests : WorkflowTestBase
{
    private const string Registry = "sys-version-registry";
    private const string LabA = "default-version-lab-a";
    private const string LabB = "default-version-lab-b";
    private const string LabC = "default-version-lab-c";
    private const string LabD = "default-version-lab-d";
    private const string LabE = "default-version-lab-e";
    private const string LabF = "default-version-lab-f";

    /// <summary>The runtime propagates a pointer change within ~5 s per pod; wait twice that.</summary>
    private static readonly TimeSpan Propagation = TimeSpan.FromSeconds(10);

    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private static readonly HashSet<string> Published = new();
    private static bool _registryPublished;

    public FlowDefaultVersionTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task NoRegistry_StartsLatest()
    {
        await EnsureVersionsAsync(LabA, "1.0.1", "1.1.0");

        var (status, body) = await StartLabAsync(LabA);

        Assert.True((int)status < 400, $"start refused with {(int)status}: {body}");
        Assert.Equal("1.1.0", FlowVersionOf(body));
    }

    [Fact]
    public async Task FullPin_StartsPinned_AndPendingNeedsExplicit()
    {
        await EnsureVersionsAsync(LabB, "1.0.1", "1.1.0");
        await EnsureRegistryAsync();
        try
        {
            await SetPointerAsync(LabB, "1.0.0");
            await WaitForVersionAsync(LabB, null, "1.0.0");

            Assert.Equal("1.0.0", await StartVersionAsync(LabB, null));
            Assert.Equal("1.0.0", await StartVersionAsync(LabB, "1.0"));

            // 1.1 exists only above the pointer: pending, not reachable by a partial request.
            var (pendingStatus, pendingBody) = await StartLabAsync(LabB, "1.1");
            Assert.Equal(HttpStatusCode.NotFound, pendingStatus);
            Assert.Contains("100051", pendingBody);

            // An explicit artifact version ignores the ceiling.
            Assert.Equal("1.1.0", await StartVersionAsync(LabB, "1.1.0"));
        }
        finally
        {
            await ClearPointerAsync(LabB);
        }
    }

    [Fact]
    public async Task LinePin_FollowsHotfix()
    {
        await EnsureVersionsAsync(LabC, "1.0.1", "1.1.0");
        await EnsureRegistryAsync();
        try
        {
            await SetPointerAsync(LabC, "1.0");

            // 1.0.1 on a first run, already 1.0.2 on a re-run: the line, never 1.1.0.
            var first = await WaitForVersionAsync(LabC, null, "1.0.1", "1.0.2");
            Assert.NotEqual("1.1.0", first);

            await EnsureVersionsAsync(LabC, "1.0.2");

            var followed = await WaitForVersionAsync(LabC, null, "1.0.2");
            Assert.Equal("1.0.2", followed);
            // The pending 1.1.0 stays out of reach of a versionless start.
            Assert.Equal("1.0.2", await StartVersionAsync(LabC, null));
        }
        finally
        {
            await ClearPointerAsync(LabC);
        }
    }

    [Fact]
    public async Task Flip_And_Rollback_DoNotMoveRunningInstances()
    {
        await EnsureVersionsAsync(LabD, "1.0.1", "1.1.0");
        await EnsureRegistryAsync();
        try
        {
            await SetPointerAsync(LabD, "1.0.0");
            await WaitForVersionAsync(LabD, null, "1.0.0");
            var (_, oldBody) = await StartLabAsync(LabD);
            var oldId = IdOf(oldBody);
            Assert.Equal("1.0.0", FlowVersionOf(oldBody));

            await SetPointerAsync(LabD, "1.1.0");
            await WaitForVersionAsync(LabD, null, "1.1.0");
            var (_, newBody) = await StartLabAsync(LabD);
            var newId = IdOf(newBody);

            Assert.Equal("1.0.0", await InstanceVersionAsync(LabD, oldId));
            await RunAcceptedAsync(LabD, oldId, "finish");
            Assert.Equal("1.0.0", await InstanceVersionAsync(LabD, oldId));

            await SetPointerAsync(LabD, "1.0.0");
            await WaitForVersionAsync(LabD, null, "1.0.0");

            Assert.Equal("1.0.0", await InstanceVersionAsync(LabD, oldId));
            Assert.Equal("1.1.0", await InstanceVersionAsync(LabD, newId));
        }
        finally
        {
            await ClearPointerAsync(LabD);
        }
    }

    [Fact]
    public async Task InvalidPointer_IsRejectedAtWrite()
    {
        await EnsureVersionsAsync(LabE, "1.0.1", "1.1.0");
        await EnsureRegistryAsync();
        try
        {
            await SetPointerAsync(LabE, "1.0.0");
            var registryId = await RegistryIdAsync(LabE);

            var (status, body) = await SendRawAsync(HttpMethod.Patch,
                $"api/v1/core/workflows/{Registry}/instances/{registryId}/transitions/set-default?sync=true",
                RegistryBody("9.9"), Headers());

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Contains("100052", body);

            var (state, instanceStatus) = await GetInstanceStateAsync(Registry, registryId);
            Assert.Equal("active", state);
            Assert.Equal("A", instanceStatus);
            var data = await GetAttributesAsync(Registry, registryId);
            Assert.Equal("1.0.0", data.GetProperty("defaultVersion").GetString());
        }
        finally
        {
            await ClearPointerAsync(LabE);
        }
    }

    [Fact]
    public async Task PointerFlip_UnderLoad_NeverStartsAnUnactivatedVersion()
    {
        await EnsureVersionsAsync(LabF, "1.0.1", "1.1.0");
        await EnsureRegistryAsync();
        try
        {
            await SetPointerAsync(LabF, "1.0.0");
            await WaitForVersionAsync(LabF, null, "1.0.0");

            using var stop = new CancellationTokenSource();
            var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
            var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var (status, body) = await StartLabAsync(LabF);
                    seen.Add((int)status < 400 ? FlowVersionOf(body) ?? "(none)" : $"HTTP {(int)status}: {body}");
                }
            })).ToArray();

            for (var i = 0; i < 3; i++)
            {
                await SetPointerAsync(LabF, "1.0.1");
                await Task.Delay(500);
                await SetPointerAsync(LabF, "1.0.0");
                await Task.Delay(500);
            }

            stop.Cancel();
            await Task.WhenAll(workers);

            Assert.NotEmpty(seen);
            Assert.All(seen, version => Assert.True(version is "1.0.0" or "1.0.1",
                $"a versionless start landed on '{version}' while the pointer only flipped 1.0.0/1.0.1"));

            await WaitForVersionAsync(LabF, null, "1.0.0");
        }
        finally
        {
            await ClearPointerAsync(LabF);
        }
    }

    [Fact(Skip = "Known gap: set-default with defaultVersion null answers 200 but the instance-data merge treats null on an existing key as no change, so the pointer is not cleared.")]
    public async Task ClearingWithNull_IsAKnownGap()
    {
        await EnsureVersionsAsync(LabF, "1.0.1", "1.1.0");
        await EnsureRegistryAsync();
        await SetPointerAsync(LabF, "1.0.0");
        await SetPointerAsync(LabF, null);
        Assert.Equal("1.1.0", await WaitForVersionAsync(LabF, null, "1.1.0"));
    }

    // ── starts ───────────────────────────────────────────────────────────────

    private Task<(HttpStatusCode Status, string Body)> StartLabAsync(string flow, string? version = null) =>
        SendRawAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{flow}/instances/start?sync=true" +
            (version is null ? "" : $"&version={version}"),
            new { }, Headers());

    private async Task<string?> StartVersionAsync(string flow, string? version)
    {
        var (status, body) = await StartLabAsync(flow, version);
        Assert.True((int)status < 400, $"start {flow} version={version ?? "(none)"} refused with {(int)status}: {body}");
        return FlowVersionOf(body);
    }

    /// <summary>
    /// Polls a start until it lands on one of <paramref name="expected"/> (a pointer change reaches
    /// each pod within ~5 s). Every probe is a real instance, so it must be one of the expected
    /// versions to count; others are retried. Returns the version it landed on.
    /// </summary>
    private async Task<string> WaitForVersionAsync(string flow, string? requested, params string[] expected)
    {
        string? last = null;
        await WaitUntilAsync(async () =>
        {
            var (status, body) = await StartLabAsync(flow, requested);
            if ((int)status >= 400)
            {
                last = $"{(int)status}: {body}";
                return false;
            }

            last = FlowVersionOf(body);
            return last is not null && expected.Contains(last);
        }, $"{flow} (version={requested ?? "none"}) never started on {string.Join("/", expected)}; last answer {last}",
            Propagation);
        return last!;
    }

    private async Task<string?> InstanceVersionAsync(string flow, string id)
    {
        var response = await Api.GetInstanceAsync(flow, id, Headers());
        return FlowVersionOf(response.Body.GetRawText());
    }

    /// <summary>The <c>flowVersion</c> of a start response or instance read (root, else metadata).</summary>
    private static string? FlowVersionOf(string raw)
    {
        var body = JsonDocument.Parse(raw).RootElement;
        if (body.TryGetProperty("flowVersion", out var top)) return Artifact(top.GetString());
        if (body.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("flowVersion", out var inner))
            return Artifact(inner.GetString());
        Assert.Fail($"response carried no flowVersion: {raw}");
        return null;
    }

    /// <summary>
    /// Artifact part of a version: an SDK-published 1.0.0 is stored as <c>1.0.0-pkg.1.0.0+core</c>, the
    /// fixture-published ones as plain <c>1.0.1</c>; compare by what precedes "-pkg." and "+".
    /// </summary>
    private static string? Artifact(string? version)
    {
        if (version is null) return null;
        var cut = version.IndexOfAny(['-', '+']);
        return cut < 0 ? version : version[..cut];
    }

    private static string IdOf(string raw) =>
        JsonDocument.Parse(raw).RootElement.GetProperty("id").GetString()!;

    // ── registry ─────────────────────────────────────────────────────────────

    private static object RegistryBody(string? pointer) =>
        new { attributes = new { type = "Flow", defaultVersion = pointer } };

    /// <summary>Starts (or finds) the registry entry of a flow. Instance key = flow key.</summary>
    private async Task<string> RegistryIdAsync(string flow)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{Registry}/instances/start?sync=true",
            new { key = flow, attributes = new { type = "Flow", defaultVersion = (string?)null } }, Headers());
        Assert.True((int)status < 400, $"registry start for {flow} refused with {(int)status}: {body}");
        return IdOf(body);
    }

    /// <summary>Points the flow at <paramref name="pointer"/>; creates the entry on first use, set-default after.</summary>
    private async Task SetPointerAsync(string flow, string? pointer)
    {
        var id = await RegistryIdAsync(flow);
        await WaitForInstanceStateAsync(Registry, id, "active");

        var (status, body) = await SendRawAsync(HttpMethod.Patch,
            $"api/v1/core/workflows/{Registry}/instances/{id}/transitions/set-default?sync=true",
            RegistryBody(pointer), Headers());
        Assert.True((int)status < 400, $"set-default {flow} -> {pointer ?? "null"} refused with {(int)status}: {body}");
    }

    /// <summary>
    /// Best-effort only: the runtime's instance-data merge treats null on an existing key as "no change"
    /// (see <see cref="ClearingWithNull_IsAKnownGap"/>), so this does NOT clear. Isolation comes from each
    /// test setting its own pointer explicitly on its own flow key.
    /// </summary>
    private async Task ClearPointerAsync(string flow)
    {
        try { await SetPointerAsync(flow, null); } catch (Exception) { /* cleanup must not mask the test result */ }
    }

    // ── publishing ───────────────────────────────────────────────────────────

    /// <summary>Publishes the registry schema, then its workflow, once per run.</summary>
    private async Task EnsureRegistryAsync()
    {
        await PublishGate.WaitAsync();
        try
        {
            if (_registryPublished) return;
            var dir = Path.Combine(RepoRoot(), "tests", "Core.IntegrationTests", "Tests", "FlowDefaultVersion", "fixtures");
            await PublishAsync(File.ReadAllText(Path.Combine(dir, "version-registry.schema.json")), "version-registry");
            await PublishAsync(File.ReadAllText(Path.Combine(dir, "sys-version-registry.workflow.json")), Registry);
            _registryPublished = true;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    /// <summary>Publishes extra versions of a lab flow, built from its 1.0.0 file under core/.</summary>
    private async Task EnsureVersionsAsync(string flow, params string[] versions)
    {
        await PublishGate.WaitAsync();
        try
        {
            foreach (var version in versions)
            {
                if (!Published.Add($"{flow}@{version}")) continue;

                var path = Path.Combine(RepoRoot(), "core", "Workflows", "flow-default-version-lab", $"{flow}.json");
                var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                definition["version"] = version;
                definition["tags"]!.AsArray().Add($"v-{version}");
                await PublishAsync(definition.ToJsonString(), $"{flow}@{version}");
            }
        }
        finally
        {
            PublishGate.Release();
        }
    }

    private async Task PublishAsync(string json, string what)
    {
        var (status, body) = await SendRawJsonAsync(HttpMethod.Post, "api/v1/definitions/publish", json, Headers());
        var alreadyThere = status == HttpStatusCode.Conflict
                           || body.Contains("already", StringComparison.OrdinalIgnoreCase);
        Assert.True((int)status < 300 || alreadyThere, $"publishing {what} answered {(int)status}: {body}");
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
