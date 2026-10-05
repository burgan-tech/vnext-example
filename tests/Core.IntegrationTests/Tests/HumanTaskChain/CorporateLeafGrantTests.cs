using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Core.IntegrationTests.Tests.CrossDomainLab;

namespace Core.IntegrationTests.Tests.HumanTaskChain;

/// <summary>
/// The human-task list against a leaf whose <c>queryRoles</c> is the issue's corporate example —
/// two <c>allOf</c> allows, OR'ed — evaluated with the PUBLIC caller's identity at the leaf, in the
/// same domain and across a domain boundary.
/// </summary>
/// <remarks>
/// <para><b>Why it exists</b> (vnext <c>feature/role-grant-combinators</c>, plan § "Yüzey gösterimleri"
/// §1). The leaf hop runs in an isolated scope with no ambient caller — and across a boundary, on
/// another host — so before this branch <c>$InstanceStarter</c>, <c>$PreviousUser</c> and the
/// behalf-of grants could never match at the leaf. The caller's <c>act_sub</c> and <c>sub</c> now
/// travel in the hop body (<c>HumanTaskLeafRequest</c>) beside the roles, and every identity leaf is
/// resolved against the LEAF instance's own <c>CreatedBy</c> / <c>CreatedByBehalfOf</c> / data (own-instance rule).</para>
/// <para><b>The leaf state</b> (<c>{level}-corporate-human</c>, reached with <c>mode = "corporate"</c>):</para>
/// <code>
/// G1 allow allOf[corporate.ops, $InstanceBehalfOfStarter]
/// G2 allow allOf[$InstanceBehalfOfStarter, $user.$.context.Instance.Data.customerId]
/// G3 allow allOf[$InstanceStarter, ht-starter-probe]     ← hop probe, not part of the issue's example
/// </code>
/// <para><b>Identity propagation is the flow author's job.</b> The runtime gives a SubFlow child the
/// caller's identity only at depth 1 (the post-commit start job runs under the ambient user). From depth
/// 2 on, <c>SubflowStarter</c> sends the child exactly the headers the parent's subflow mapping returns,
/// so the chain's <c>HtXToNextSubFlowMapping</c>s forward <c>sub</c> and <c>act_sub</c> explicitly
/// (own-instance rule). Without that a deeper leaf records no creator and the identity leaves cannot match.</para>
/// <para><b>The case</b>: started by ALİ (<c>act_sub=u-ali</c>) on behalf of <c>c-acme</c>
/// (<c>sub</c>), about the customer <c>u-veli</c>. Every subflow mapping carries <c>customerId</c>
/// down, so the leaf's own data has it.</para>
/// </remarks>
public class CorporateLeafGrantTests(VNextTestEnvironment environment, CrossDomainLabFixture lab, HumanTaskChainFixture credit)
    : WorkflowTestBase(environment), IClassFixture<CrossDomainLabFixture>, IClassFixture<HumanTaskChainFixture>
{
    private const string Root = "ht-a";

    /// <summary>A caller: role header (or none), actor (<c>act_sub</c>) and subject (<c>sub</c>).</summary>
    private sealed record Caller(string? Roles, string ActSub, string Sub);

    private static readonly Caller Starter = new("ht-approver", "u-ali", "c-acme");
    private static readonly Caller Ops = new("corporate.ops", "u-ops", "c-acme");
    private static readonly Caller Veli = new(null, "u-veli", "c-acme");                  // role-less
    private static readonly Caller AliAsCustomer = new("customer-role", "u-ali", "u-ali"); // non-matching
    private static readonly Caller Anon = new(null, "u-x", "u-x");
    private static readonly Caller ProbeStarter = new("ht-starter-probe", "u-ali", "u-ali");
    private static readonly Caller ProbeStranger = new("ht-starter-probe", "u-x", "u-x");

    private static Dictionary<string, string> As(Caller caller)
    {
        var headers = Headers(caller.Roles);
        headers["act_sub"] = caller.ActSub;
        headers["sub"] = caller.Sub;
        return headers;
    }

    private static string CoreBaseUrl =>
        System.Environment.GetEnvironmentVariable("VNEXT_BASE_URL")?.TrimEnd('/') ?? "http://localhost:4201";

    /// <summary>The ids of the rows <paramref name="caller"/> is shown by core's list, read fresh.</summary>
    private async Task<IReadOnlyList<string>> VisibleRowIdsAsync(Caller caller)
    {
        var headers = As(caller);
        headers["X-VNext-Cache-Override"] = "true";

        var (status, body) = await SendRawAsync(HttpMethod.Get, "api/v1/core/functions/human-task", headers: headers);
        Assert.True(status == HttpStatusCode.OK, $"human-task function failed: {status} {body}");

        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.EnumerateArray()
            .Select(row => row.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty)];
    }

    /// <summary>
    /// Starts a corporate case AS the starter and returns the root id plus the leaf's flow and id once
    /// the leaf rests in its corporate human state.
    /// </summary>
    private async Task<(string RootId, string LeafFlow, string LeafId)> StartCorporateCaseAsync(int hops, string leafFlow)
    {
        var response = await Api.StartInstanceAsync(Root, new
        {
            hops,
            mode = "corporate",
            customerId = "u-veli",
            testId = Guid.NewGuid().ToString("N"),
            humanTask = new { title = "HT-A step", description = "HT-A step description" }
        }, As(Starter));
        var rootId = response.Body.GetProperty("id").GetString()!;
        await AssertNotFaultedAsync(Root, rootId, Starter.Roles);

        string? leafId = null;
        await WaitUntilAsync(async () =>
        {
            leafId = await TryWalkToAsync(rootId, leafFlow);
            if (leafId is null) return false;
            var metadata = await LeafMetadataAsync(leafFlow, leafId);
            return metadata.TryGetProperty("currentState", out var state)
                   && state.GetString() == $"{leafFlow}-corporate-human";
        }, $"chain {rootId} (hops={hops}) never came to rest in {leafFlow}-corporate-human", TimeSpan.FromMinutes(2));

        return (rootId, leafFlow, leafId!);
    }

    /// <summary>Follows the active correlations from the root (all hops before the leaf are in core).</summary>
    private async Task<string?> TryWalkToAsync(string rootId, string targetFlow)
    {
        var flow = Root;
        var id = rootId;
        for (var depth = 0; depth < 6 && flow != targetFlow; depth++)
        {
            var (status, body) = await SendRawAsync(HttpMethod.Get,
                $"api/v1/core/workflows/{flow}/instances/{id}/functions/state", headers: As(Starter));
            if (status != HttpStatusCode.OK) return null;

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("activeCorrelations", out var correlations)
                || correlations.GetArrayLength() == 0)
                return null;

            flow = correlations[0].GetProperty("subFlowName").GetString()!;
            id = correlations[0].GetProperty("subFlowInstanceId").GetString()!;
        }

        return flow == targetFlow ? id : null;
    }

    /// <summary>The leaf instance's <c>metadata</c> block from the domain that owns it.</summary>
    private async Task<JsonElement> LeafMetadataAsync(string leafFlow, string leafId)
    {
        var (baseUrl, domain) = leafFlow == "ht-d" ? (lab.PartnerBaseUrl!, "partner") : (CoreBaseUrl, "core");

        using var client = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/{domain}/workflows/{leafFlow}/instances/{leafId}");
        foreach (var (key, value) in As(Starter)) request.Headers.TryAddWithoutValidation(key, value);

        using var result = await client.SendAsync(request);
        var body = await result.Content.ReadAsStringAsync();
        if (result.StatusCode != HttpStatusCode.OK) return default;

        var root = JsonDocument.Parse(body).RootElement.Clone();
        return root.TryGetProperty("metadata", out var metadata) ? metadata : default;
    }

    /// <summary>
    /// Own-instance-rule precondition, asserted before any visibility claim: the identity leaves resolve against the
    /// LEAF's own instance, so the leaf must have been created under the starter's identity. A red here
    /// is a finding about how a SubFlow child records its creator, not about the list.
    /// </summary>
    private async Task AssertLeafCarriesTheStartersIdentityAsync(string leafFlow, string leafId)
    {
        var metadata = await LeafMetadataAsync(leafFlow, leafId);
        string? Read(string name) =>
            metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var v) ? v.GetString() : null;

        Assert.True(Read("createdBy") == Starter.ActSub && Read("createdByBehalfOf") == Starter.Sub,
            $"{leafFlow}/{leafId} was created by '{Read("createdBy")}' on behalf of '{Read("createdByBehalfOf")}', " +
            $"not by the root's starter ({Starter.ActSub} / {Starter.Sub}); $InstanceStarter / " +
            "$InstanceBehalfOfStarter evaluate against the leaf's own instance (own-instance rule), so the corporate grants " +
            "cannot match until the child records the starter");
    }

    private async Task AssertCorporateVisibilityAsync(string rootId)
    {
        // Positive halves first, polled: the projection that makes the root listable may trail the leaf.
        await WaitUntilAsync(async () => (await VisibleRowIdsAsync(Ops)).Contains(rootId),
            $"OPS (corporate.ops, sub = behalf-of starter) never saw {rootId}: G1 = Yes ∧ Yes should admit",
            TimeSpan.FromMinutes(1));

        Assert.Contains(rootId, await VisibleRowIdsAsync(Veli));        // G2 = Yes ∧ Yes (G1 = Unknown ∧ Yes)
        Assert.DoesNotContain(rootId, await VisibleRowIdsAsync(AliAsCustomer)); // G1 No, G2 No, G3 No
        Assert.DoesNotContain(rootId, await VisibleRowIdsAsync(Anon));          // G1 Unknown ∧ No = No; G2 No

        // Hop probe: $InstanceStarter is evaluated against the caller's act_sub AT THE LEAF.
        Assert.Contains(rootId, await VisibleRowIdsAsync(ProbeStarter));
        Assert.DoesNotContain(rootId, await VisibleRowIdsAsync(ProbeStranger));
    }

    /// <summary>
    /// Depth 1: A → B, leaf <c>ht-b-corporate-human</c> in core. The one level where the runtime itself
    /// gives the child the caller's identity (the post-commit start job runs under the ambient user), so
    /// this proves the grant logic independently of the fixture's identity-forwarding mappings.
    /// </summary>
    [SkippableFact]
    public async Task CorporateLeaf_DepthOne_TheRuntimeInheritsTheIdentity()
    {
        var (rootId, leafFlow, leafId) = await StartCorporateCaseAsync(hops: 1, leafFlow: "ht-b");

        await AssertLeafCarriesTheStartersIdentityAsync(leafFlow, leafId);
        await AssertCorporateVisibilityAsync(rootId);
    }

    /// <summary>
    /// Same domain: A → B → C, leaf <c>ht-c-corporate-human</c> in core. From depth 2 on the child is
    /// created with exactly the headers its parent's subflow mapping returns, so the leaf records the
    /// starter only because every <c>HtXToNextSubFlowMapping</c> forwards <c>sub</c> / <c>act_sub</c>. Even here the leaf is
    /// evaluated in an isolated scope, so the identity must travel in the request.
    /// </summary>
    [SkippableFact]
    public async Task CorporateLeaf_SameDomain_TwoAllOfGrantsDecideVisibility()
    {
        var (rootId, leafFlow, leafId) = await StartCorporateCaseAsync(hops: 2, leafFlow: "ht-c");

        await AssertLeafCarriesTheStartersIdentityAsync(leafFlow, leafId);
        await AssertCorporateVisibilityAsync(rootId);
    }

    /// <summary>
    /// One boundary: A → B → C (core) → D (partner), leaf <c>ht-d-corporate-human</c>. Core's list
    /// descends to partner over HTTP; the remote leaf sees the caller's <c>act_sub</c> / <c>sub</c>
    /// only because they ride in the hop body. The probe row is the direct proof for
    /// <c>$InstanceStarter</c>: only the starter's act_sub matches the remote leaf's <c>CreatedBy</c>.
    /// </summary>
    [SkippableFact]
    public async Task CorporateLeaf_AcrossADomainBoundary_TheCallersIdentityTravelsInTheHop()
    {
        Skip.If(lab.PartnerBaseUrl is null, "partner domain not configured — run labs/cross-domain/lab.sh up");
        _ = credit; // the credit fixture is taken for parity with the scenario's other class; not used here

        var (rootId, leafFlow, leafId) = await StartCorporateCaseAsync(hops: 3, leafFlow: "ht-d");

        await AssertLeafCarriesTheStartersIdentityAsync(leafFlow, leafId);
        await AssertCorporateVisibilityAsync(rootId);
    }
}
