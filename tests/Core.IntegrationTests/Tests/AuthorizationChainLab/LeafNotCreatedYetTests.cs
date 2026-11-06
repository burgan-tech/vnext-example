using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AuthorizationChainLab;

/// <summary>
/// vnext#1181: <c>authorize?queryRoles=true</c> must not deny while an active SubFlow's child is still
/// being created. The deepest EXISTING instance decides until the child's row exists.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A parent commits its SubFlow correlation in its own transaction
/// (<c>HandleSubFlowStep</c>); the child's row is created afterwards by the post-commit
/// <c>StartSubflowJob</c>, after the subflow input mapping runs. Inside that window the descent reached a
/// child that could not be loaded, and the child's "not found" was passed up as a deny — the gateway
/// turned it into a 403 on the client's state poll (seen on the shared test environment in credit,
/// onboarding and morph-idm). The fix: the child answers <c>reason: "instance-not-found"</c> and the
/// parent decides with its own <c>queryRoles</c>.</para>
/// <para><b>Shape.</b> <c>root-slow</c> (admits <c>chain.admin</c>) → <c>mid-slow</c> (admits
/// <c>chain.mid-only</c>) → <c>leaf-open</c> (declares no <c>queryRoles</c>). The mid auto-transitions
/// into the state that starts the leaf, and that start's input mapping waits six seconds — the issue's
/// exact shape with the window held open. Each verdict names the level that took it.</para>
/// </remarks>
public sealed class LeafNotCreatedYetTests : AuthorizationChainLabTestBase
{
    private const string RootSlow = "authorization-chain-lab-root-slow";
    private const string MidSlow = "authorization-chain-lab-mid-slow";

    public LeafNotCreatedYetTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// Inside the window the mid — the deepest instance that exists — decides: <c>chain.mid-only</c> is
    /// allowed at the root (before the fix: denied, the issue's 403), <c>chain.admin</c>, which only the
    /// root admits, is refused (proves the fallback is the deepest existing level, not the root and not
    /// a blanket allow). Once the leaf exists its empty grant set decides and everyone is allowed.
    /// </summary>
    [Fact]
    public async Task WhileTheLeafIsBeingCreatedTheDeepestExistingInstanceDecides()
    {
        // ?sync=false: a sync start (the SDK's default) returns only once the whole chain has come to
        // rest — after the leaf exists, i.e. after the window this test is about has closed.
        var rootId = await StartWithoutWaitingAsync(RootSlow);

        string? midId = null;
        await WaitUntilAsync(async () =>
        {
            var subs = await GetActiveSubflowsAsync(RootSlow, rootId, Admin);
            return subs.TryGetValue(MidSlow, out midId);
        }, $"{RootSlow} {rootId} should open a correlation to {MidSlow}", TimeSpan.FromSeconds(20));

        // The mid's correlation to the leaf is committed before the leaf's row exists.
        string? leafId = null;
        await WaitUntilAsync(async () =>
        {
            var subs = await GetActiveSubflowsAsync(MidSlow, midId, MidOnly);
            return subs.TryGetValue(LeafOpen, out leafId);
        }, $"{MidSlow} {midId} should open a correlation to {LeafOpen}", TimeSpan.FromSeconds(20));

        // ── inside the window ────────────────────────────────────────────────
        // Probe first: the leaf really is not loadable, and it says so without evaluating a grant.
        var (leafStatus, leafBody) = await AuthorizeAsync(LeafOpen, leafId!, MidOnly, queryRoles: true);
        Assert.Equal(HttpStatusCode.Forbidden, leafStatus);
        Assert.Equal("instance-not-found", ReasonOf(leafBody));

        var midOnlyAtRoot = await IsAuthorizedAsync(RootSlow, rootId, MidOnly, queryRoles: true);
        var adminAtRoot = await IsAuthorizedAsync(RootSlow, rootId, Admin, queryRoles: true);
        var midOnlyAtMid = await IsAuthorizedAsync(MidSlow, midId, MidOnly, queryRoles: true);

        // Still inside the window after the probes, or the verdicts above were taken on a live leaf.
        var (stillMissing, stillBody) = await AuthorizeAsync(LeafOpen, leafId!, MidOnly, queryRoles: true);
        Assert.True(stillMissing == HttpStatusCode.Forbidden && ReasonOf(stillBody) == "instance-not-found",
            "the leaf was created before the probes finished; widen the mapping's delay");

        Assert.True(midOnlyAtRoot,
            "chain.mid-only is admitted by the mid, the deepest existing instance; a refusal is the " +
            "vnext#1181 deny (the leaf's 'not found' passed up as a verdict)");
        Assert.False(adminAtRoot,
            "chain.admin is admitted only by the root; an allow means the fallback decided at the " +
            "root (or allowed blindly) instead of at the deepest existing instance");
        Assert.True(midOnlyAtMid, "the mid addressed directly must answer the same as through the root");

        // ── after the window ─────────────────────────────────────────────────
        await WaitUntilAsync(async () =>
        {
            var (status, body) = await AuthorizeAsync(LeafOpen, leafId!, NoRole, queryRoles: true);
            return ReasonOf(body) is null && status == HttpStatusCode.OK;
        }, $"{LeafOpen} {leafId} should be created once the slow mapping returns", TimeSpan.FromSeconds(30));

        Assert.True(await IsAuthorizedAsync(RootSlow, rootId, Admin, queryRoles: true),
            "the leaf exists now and declares no queryRoles: an empty grant set allows");
        Assert.True(await IsAuthorizedAsync(RootSlow, rootId, NoRole, queryRoles: true),
            "the leaf decides once it exists, for a role-less caller too");
    }

    /// <summary>Starts with <c>?sync=false</c>; the SDK client hard-codes <c>?sync=true</c>.</summary>
    private async Task<string> StartWithoutWaitingAsync(string workflow)
    {
        var (status, raw) = await SendRawAsync(
            HttpMethod.Post, $"api/v1/core/workflows/{workflow}/instances/start?sync=false",
            new { chainRef = $"slow-{Guid.NewGuid():N}"[..24] }, Headers(Admin));

        Assert.True((int)status < 400, $"start was refused with {(int)status}: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("start response carried no instance id");
    }

    private static string? ReasonOf(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reason", out var reason)
            ? reason.GetString()
            : null;
}
