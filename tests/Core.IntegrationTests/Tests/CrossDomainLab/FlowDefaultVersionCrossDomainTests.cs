using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// XV-01..XV-06 of labs/cross-domain/VNEXT-BUILD-PLAN-flow-default-version-xd.md: a core parent consumes
/// partner's <c>fdv-xd-contract</c> through a SubFlow state and a SubProcess task, and the child's
/// version comes from <b>partner's</b> <c>sys-version-registry</c> pointer — resolution happens on the
/// callee side. XV-07 (the <c>vnext.flow.version.source</c> span tag) is checked by hand in OpenObserve.
/// <para>
/// Versions on the wire: <c>fdv-xd-contract 1.2.0</c> comes from <c>partner/</c> (stored as
/// <c>1.2.0-pkg.1.0.0+partner</c>) and <c>fdv-xd-onboarding 1.0.0</c> from <c>core/</c>
/// (<c>1.0.0-pkg.…+core</c>); <c>fdv-xd-contract 1.3.0</c> and <c>fdv-xd-onboarding 1.1.0</c>
/// (contract reference pinned to <c>"1.3.0"</c>) are built here from those files and published
/// through <c>api/v1/definitions/publish</c>, as plain versions. The registry flow is not in the core
/// system package yet, so the FlowDefaultVersion fixture copy is published into BOTH domains (with
/// <c>domain</c> rewritten for partner). Publishing is version-immutable; "already published" is fine.
/// </para>
/// <para>
/// Because 1.1.0 exists, a versionless <c>fdv-xd-onboarding</c> start lands on 1.1.0 whenever core
/// has no pointer; every test except XV-03 therefore starts the onboarding with an explicit
/// <c>?version=1.0.0</c> so the floating <c>"1"</c> reference is what is exercised.
/// </para>
/// </summary>
[Collection("VNextIntegration")]
public class FlowDefaultVersionCrossDomainTests : CrossDomainLabTestBase, IClassFixture<CrossDomainLabFixture>
{
    private const string Registry = "sys-version-registry";
    private const string Contract = "fdv-xd-contract";
    private const string Onboarding = "fdv-xd-onboarding";
    private const string OnboardingExact = "fdv-xd-onboarding-exact";
    private const string OnboardingSp = "fdv-xd-onboarding-sp";

    /// <summary>A pointer change reaches each pod within ~5 s; wait twice that.</summary>
    private static readonly TimeSpan Propagation = TimeSpan.FromSeconds(10);

    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private static bool _published;

    private readonly HttpClient? _partnerRaw;

    public FlowDefaultVersionCrossDomainTests(VNextTestEnvironment environment, CrossDomainLabFixture lab)
        : base(environment, lab)
    {
        if (lab.PartnerBaseUrl is not null)
            _partnerRaw = new HttpClient { BaseAddress = new Uri(lab.PartnerBaseUrl.TrimEnd('/') + "/") };
    }

    /// <summary>XV-01: a floating "1" reference follows the callee's default; the correlation holds the child's resolved version.</summary>
    [SkippableFact]
    public async Task FloatingReference_FollowsCalleeDefault()
    {
        RequirePartner();
        await EnsurePublishedAsync();
        try
        {
            await PinPartnerContractAsync("1.2.0");

            var started = await StartSubFlowParentAsync(Onboarding, "1.0.0");

            Assert.Equal("1.2.0", Artifact(started.ChildVersion));
            Assert.StartsWith("1.2.0-pkg.", started.CorrelationVersion);

            // The parent finds its child again through the recorded version: signing the child resumes it.
            await RunPartnerAsync(Contract, started.ChildId, "sign");
            await WaitForInstanceStateAsync(Onboarding, started.ParentId, "onb-done", timeout: TimeSpan.FromSeconds(60));
        }
        finally
        {
            await ClearPointerAsync(Partner, Contract);
        }
    }

    /// <summary>XV-02: an exact "1.2.0" reference ignores the callee's default, before and after it moves.</summary>
    [SkippableFact]
    public async Task ExactReference_IgnoresCalleeDefault()
    {
        RequirePartner();
        await EnsurePublishedAsync();
        try
        {
            await PinPartnerContractAsync("1.2.0");
            var before = await StartSubFlowParentAsync(OnboardingExact, null);
            Assert.Equal("1.2.0", Artifact(before.ChildVersion));

            await PinPartnerContractAsync("1.3", "1.3.0");
            var after = await StartSubFlowParentAsync(OnboardingExact, null);
            Assert.Equal("1.2.0", Artifact(after.ChildVersion));
            Assert.StartsWith("1.2.0-pkg.", after.CorrelationVersion);
        }
        finally
        {
            await ClearPointerAsync(Partner, Contract);
        }
    }

    /// <summary>XV-03: both domains run a pilot — core picks the onboarding version, partner the contract version.</summary>
    [SkippableFact]
    public async Task PilotInBothDomains()
    {
        RequirePartner();
        await EnsurePublishedAsync();
        try
        {
            await PinPartnerContractAsync("1.2.0");
            await SetPointerAsync(Core, Onboarding, "1.0.0");

            // Versionless start: core's pointer → onboarding 1.0.0 (floating "1") → partner's pointer → 1.2.0.
            Child? pinned = null;
            await WaitUntilAsync(async () =>
            {
                pinned = await StartSubFlowParentAsync(Onboarding, null);
                return Artifact(pinned.ParentVersion) == "1.0.0";
            }, $"versionless {Onboarding} never landed on 1.0.0; last {pinned?.ParentVersion}", Propagation);
            Assert.Equal("1.2.0", Artifact(pinned!.ChildVersion));

            // Explicit pending version: onboarding 1.1.0 references the contract exactly at 1.3.0.
            var pilot = await StartSubFlowParentAsync(Onboarding, "1.1.0");
            Assert.Equal("1.1.0", Artifact(pilot.ParentVersion));
            Assert.Equal("1.3.0", Artifact(pilot.ChildVersion));
            Assert.Equal("1.3.0", pilot.CorrelationVersion);
        }
        finally
        {
            await ClearPointerAsync(Core, Onboarding);
            await ClearPointerAsync(Partner, Contract);
        }
    }

    /// <summary>XV-04: the callee's default moves; new children follow within 10 s, a running child and its correlation stay.</summary>
    [SkippableFact]
    public async Task CalleeDefaultChange_NewChildrenFollow_RunningChildStays()
    {
        RequirePartner();
        await EnsurePublishedAsync();
        try
        {
            await PinPartnerContractAsync("1.2.0");
            var old = await StartSubFlowParentAsync(Onboarding, "1.0.0");
            Assert.Equal("1.2.0", Artifact(old.ChildVersion));

            await SetPointerAsync(Partner, Contract, "1.3");
            var fresh = await WaitForChildVersionAsync(() => StartSubFlowParentAsync(Onboarding, "1.0.0"), "1.3.0");
            Assert.Equal("1.3.0", fresh.CorrelationVersion);

            await AssertChildUnmovedAsync(Onboarding, old);
        }
        finally
        {
            await ClearPointerAsync(Partner, Contract);
        }
    }

    /// <summary>XV-05: a SubProcess task with version "1" obeys the same rule (XV-01 + XV-04 on the SubProcess path).</summary>
    [SkippableFact]
    public async Task SubProcess_FollowsCalleeDefault_RunningChildStays()
    {
        RequirePartner();
        await EnsurePublishedAsync();
        try
        {
            await PinPartnerContractAsync("1.2.0");
            var old = await StartSubProcessParentAsync();
            Assert.Equal("1.2.0", Artifact(old.ChildVersion));
            Assert.StartsWith("1.2.0-pkg.", old.CorrelationVersion);

            await SetPointerAsync(Partner, Contract, "1.3");
            var fresh = await WaitForChildVersionAsync(StartSubProcessParentAsync, "1.3.0");
            Assert.Equal("1.3.0", fresh.CorrelationVersion);

            await AssertChildUnmovedAsync(OnboardingSp, old);
        }
        finally
        {
            await ClearPointerAsync(Partner, Contract);
        }
    }

    /// <summary>XV-06: "latest" returns the callee to automatic — the newest published version (1.3.0).</summary>
    [SkippableFact]
    public async Task Latest_ReturnsCalleeToAutomatic()
    {
        RequirePartner();
        await EnsurePublishedAsync();
        try
        {
            await PinPartnerContractAsync("1.2.0");

            await SetPointerAsync(Partner, Contract, "latest");
            var fresh = await WaitForChildVersionAsync(() => StartSubFlowParentAsync(Onboarding, "1.0.0"), "1.3.0");
            Assert.Equal("1.3.0", fresh.CorrelationVersion);
        }
        finally
        {
            await ClearPointerAsync(Partner, Contract);
        }
    }

    // ── parents and children ─────────────────────────────────────────────────

    /// <summary>A started parent and the partner child it opened, all versions exactly as the runtime stored them.</summary>
    private sealed record Child(string ParentId, string ParentVersion, string ChildId, string ChildVersion, string CorrelationVersion);

    private Task<Child> StartSubFlowParentAsync(string flow, string? version) => StartParentAndChildAsync(flow, version);

    private Task<Child> StartSubProcessParentAsync() => StartParentAndChildAsync(OnboardingSp, null);

    private async Task<Child> StartParentAndChildAsync(string flow, string? version)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Post,
            $"api/v1/core/workflows/{flow}/instances/start?sync=true" + (version is null ? "" : $"&version={version}"),
            new { }, Headers());
        Assert.True((int)status < 400, $"start {flow} version={version ?? "(none)"} refused with {(int)status}: {body}");

        var root = JsonDocument.Parse(body).RootElement;
        var parentId = root.GetProperty("id").GetString()!;
        var parentVersion = RawFlowVersionOf(root, body);

        var correlation = await WaitForContractCorrelationAsync(flow, parentId);
        var childId = correlation.GetProperty("subFlowInstanceId").GetString()!;
        Assert.Equal("partner", Str(correlation, "subFlowDomain"));

        return new Child(parentId, parentVersion, childId,
            await PartnerChildVersionAsync(childId),
            correlation.GetProperty("subFlowVersion").GetString()!);
    }

    /// <summary>The parent's open correlation to the partner contract, from the core state function.</summary>
    private async Task<JsonElement> WaitForContractCorrelationAsync(string flow, string parentId)
    {
        JsonElement found = default;
        await WaitUntilAsync(async () =>
        {
            var response = await Api.CallInstanceFunctionAsync(flow, parentId, "state", headers: Headers());
            if (!response.Body.TryGetProperty("activeCorrelations", out var correlations)
                || correlations.ValueKind != JsonValueKind.Array) return false;

            foreach (var correlation in correlations.EnumerateArray())
            {
                if (Str(correlation, "subFlowName") != Contract) continue;
                found = correlation.Clone();
                return true;
            }
            return false;
        }, $"{flow}/{parentId} never showed an active correlation to {Contract}: {await DescribeAsync(flow, parentId)}",
            TimeSpan.FromSeconds(30));
        return found;
    }

    private async Task<string> PartnerChildVersionAsync(string childId)
    {
        var response = await PartnerApi.GetInstanceAsync(Contract, childId, Headers());
        Assert.True((int)response.StatusCode < 400, $"partner {Contract}/{childId} read answered {(int)response.StatusCode}");
        return RawFlowVersionOf(response.Body, response.Body.GetRawText());
    }

    /// <summary>
    /// Starts new parents until one's child lands on <paramref name="expected"/> — the pointer change
    /// must reach the callee within <see cref="Propagation"/>. Every probe is a real parent + child.
    /// </summary>
    private static async Task<Child> WaitForChildVersionAsync(Func<Task<Child>> start, string expected)
    {
        Child? last = null;
        await WaitUntilAsync(async () =>
        {
            last = await start();
            return Artifact(last.ChildVersion) == expected;
        }, $"no new child started on {expected} within {Propagation.TotalSeconds:0}s; last child {last?.ChildVersion}", Propagation);
        return last!;
    }

    /// <summary>A child started before the pointer moved still runs its version, and its parent's correlation still says so.</summary>
    private async Task AssertChildUnmovedAsync(string flow, Child old)
    {
        Assert.Equal(old.ChildVersion, await PartnerChildVersionAsync(old.ChildId));
        var correlation = await WaitForContractCorrelationAsync(flow, old.ParentId);
        Assert.Equal(old.ChildId, correlation.GetProperty("subFlowInstanceId").GetString());
        Assert.Equal(old.CorrelationVersion, correlation.GetProperty("subFlowVersion").GetString());
        Assert.StartsWith("1.2.0-pkg.", old.CorrelationVersion);
    }

    private async Task RunPartnerAsync(string flow, string id, string transition)
    {
        var (status, body) = await PartnerSendAsync(HttpMethod.Patch,
            $"api/v1/partner/workflows/{flow}/instances/{id}/transitions/{transition}?sync=true", "{}");
        Assert.True((int)status < 400, $"partner {flow}/{id} '{transition}' refused with {(int)status}: {body}");
    }

    // ── versions ─────────────────────────────────────────────────────────────

    /// <summary>The <c>flowVersion</c> exactly as stored (root of a start response, else metadata).</summary>
    private static string RawFlowVersionOf(JsonElement body, string raw)
    {
        if (body.TryGetProperty("flowVersion", out var top) && top.ValueKind == JsonValueKind.String) return top.GetString()!;
        if (body.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("flowVersion", out var inner))
            return inner.GetString()!;
        Assert.Fail($"response carried no flowVersion: {raw}");
        return "";
    }

    /// <summary>Artifact part of a version: <c>1.2.0-pkg.1.0.0+partner</c> → <c>1.2.0</c>.</summary>
    private static string Artifact(string version)
    {
        var cut = version.IndexOfAny(['-', '+']);
        return cut < 0 ? version : version[..cut];
    }

    // ── registry (per domain) ────────────────────────────────────────────────

    private const string Core = "core";
    private const string Partner = "partner";

    private Task<(HttpStatusCode Status, string Body)> SendToAsync(string domain, HttpMethod method, string url, string json) =>
        domain == Partner
            ? PartnerSendAsync(method, url, json)
            : SendRawJsonAsync(method, url, json, Headers());

    private static string RegistryBody(string? pointer) =>
        JsonSerializer.Serialize(new { attributes = new { type = "Flow", defaultVersion = pointer } });

    /// <summary>Starts (or finds) the registry entry of a flow in a domain. Instance key = flow key.</summary>
    private async Task<string> RegistryIdAsync(string domain, string flow)
    {
        var json = JsonSerializer.Serialize(new { key = flow, attributes = new { type = "Flow", defaultVersion = (string?)null } });
        var (status, body) = await SendToAsync(domain, HttpMethod.Post,
            $"api/v1/{domain}/workflows/{Registry}/instances/start?sync=true", json);
        Assert.True((int)status < 400, $"{domain} registry start for {flow} refused with {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Points <paramref name="flow"/> in <paramref name="domain"/> at <paramref name="pointer"/> (create on first use, set-default after).</summary>
    private async Task SetPointerAsync(string domain, string flow, string pointer)
    {
        var id = await RegistryIdAsync(domain, flow);
        string? last = null;
        await WaitUntilAsync(async () =>
        {
            var (status, body) = await SendToAsync(domain, HttpMethod.Patch,
                $"api/v1/{domain}/workflows/{Registry}/instances/{id}/transitions/set-default?sync=true",
                RegistryBody(pointer));
            last = $"{(int)status}: {body}";
            // 409 Busy right after the create is the only retryable answer; anything else is final.
            if (status == HttpStatusCode.Conflict && body.Contains("Busy", StringComparison.OrdinalIgnoreCase)) return false;
            Assert.True((int)status < 400, $"{domain} set-default {flow} -> {pointer} refused with {last}");
            return true;
        }, $"{domain} set-default {flow} -> {pointer} never accepted; last {last}", TimeSpan.FromSeconds(15));
    }

    /// <summary>Partner pointer for the contract, then waits until a direct partner start resolves to it.</summary>
    private async Task PinPartnerContractAsync(string pointer, string? expected = null)
    {
        expected ??= pointer;
        await SetPointerAsync(Partner, Contract, pointer);

        string? last = null;
        await WaitUntilAsync(async () =>
        {
            var (status, body) = await PartnerSendAsync(HttpMethod.Post,
                $"api/v1/partner/workflows/{Contract}/instances/start?sync=true", "{}");
            if ((int)status >= 400) { last = $"{(int)status}: {body}"; return false; }
            last = RawFlowVersionOf(JsonDocument.Parse(body).RootElement, body);
            return Artifact(last) == expected;
        }, $"partner {Contract} never started on {expected} after pointer {pointer}; last {last}", Propagation);
    }

    /// <summary>"latest" returns to Automatic (null on an existing key is a no-op in the instance-data merge).</summary>
    private async Task ClearPointerAsync(string domain, string flow)
    {
        try { await SetPointerAsync(domain, flow, "latest"); } catch (Exception) { /* cleanup must not mask the test result */ }
    }

    // ── publishing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Once per run: the registry (schema, then workflow) into core AND partner, partner's contract
    /// 1.3.0 and core's onboarding 1.1.0 (contract reference pinned to "1.3.0").
    /// </summary>
    private async Task EnsurePublishedAsync()
    {
        await PublishGate.WaitAsync();
        try
        {
            if (_published) return;

            var root = RepoRoot();
            var fixtures = Path.Combine(root, "tests", "Core.IntegrationTests", "Tests", "FlowDefaultVersion", "fixtures");
            var schema = File.ReadAllText(Path.Combine(fixtures, "version-registry.schema.json"));
            var registry = File.ReadAllText(Path.Combine(fixtures, "sys-version-registry.workflow.json"));

            await PublishAsync(Core, schema, "core version-registry");
            await PublishAsync(Core, registry, $"core {Registry}");
            await PublishAsync(Partner, InDomain(schema, Partner), "partner version-registry");
            await PublishAsync(Partner, InDomain(registry, Partner), $"partner {Registry}");

            var contract = JsonNode.Parse(File.ReadAllText(
                Path.Combine(root, "partner", "Workflows", "flow-default-version-xd", $"{Contract}.json")))!.AsObject();
            contract["version"] = "1.3.0";
            contract["tags"]!.AsArray().Add("v-1.3.0");
            contract["attributes"]!["labels"]!.AsArray()[0]!["label"] = "Flow Default Version XD Contract (1.3.0)";
            await PublishAsync(Partner, contract.ToJsonString(), $"{Contract}@1.3.0");

            var onboarding = JsonNode.Parse(File.ReadAllText(
                Path.Combine(root, "core", "Workflows", "flow-default-version-xd", $"{Onboarding}.json")))!.AsObject();
            onboarding["version"] = "1.1.0";
            onboarding["tags"]!.AsArray().Add("v-1.1.0");
            onboarding["attributes"]!["labels"]!.AsArray()[0]!["label"] = "Flow Default Version XD Onboarding (1.1.0, exact \"1.3.0\")";
            var subFlowState = onboarding["attributes"]!["states"]!.AsArray()
                .Single(s => s!["key"]!.GetValue<string>() == "onb-contract")!;
            subFlowState["subFlow"]!["process"]!["version"] = "1.3.0";
            await PublishAsync(Core, onboarding.ToJsonString(), $"{Onboarding}@1.1.0");

            _published = true;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    /// <summary>Rewrites every <c>"domain": "core"</c> of a fixture component to <paramref name="domain"/>.</summary>
    private static string InDomain(string json, string domain)
    {
        var node = JsonNode.Parse(json)!;
        Rewrite(node);
        return node.ToJsonString();

        void Rewrite(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var (name, value) in obj.ToList())
                    {
                        if (name == "domain" && value is JsonValue v && v.TryGetValue<string>(out var s) && s == Core)
                            obj[name] = domain;
                        else
                            Rewrite(value);
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array) Rewrite(item);
                    break;
            }
        }
    }

    private async Task PublishAsync(string domain, string json, string what)
    {
        var (status, body) = await SendToAsync(domain, HttpMethod.Post, "api/v1/definitions/publish", json);
        var alreadyThere = status == HttpStatusCode.Conflict
                           || body.Contains("already", StringComparison.OrdinalIgnoreCase);
        Assert.True((int)status < 300 || alreadyThere, $"publishing {what} to {domain} answered {(int)status}: {body}");
    }

    private async Task<(HttpStatusCode Status, string Body)> PartnerSendAsync(HttpMethod method, string url, string json)
    {
        var client = _partnerRaw ?? throw new InvalidOperationException("partner client unavailable");
        using var request = new HttpRequestMessage(method, url.TrimStart('/'))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        foreach (var (key, value) in Headers()) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
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
