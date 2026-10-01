using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AuthorizationChainLab;

/// <summary>
/// The removal: the runtime's own <c>queryRoles</c> and long-poll acknowledge gates are gone from
/// every surface that carried them, while <c>authorize</c> — the oracle the Internal Gateway
/// consults — keeps answering exactly as before.
/// </summary>
/// <remarks>
/// <para><b>What moved and what did not.</b> Authorization for these surfaces now happens at the
/// Internal Gateway, which asks <c>GET .../functions/authorize?queryRoles=true</c> (and
/// <c>?ack=true</c>) before the request reaches the runtime. `queryRoles` itself is untouched: it is
/// still evaluated in full, per hop down the active-correlation chain, by that endpoint. What was
/// deleted is the runtime's SECOND copy of the decision — not the decision.</para>
/// <para><b>Why the oracle test is the important one here.</b> A removal is easy to see in a diff;
/// what is not easy to see is a removal that went one surface too far. If <c>authorize</c> ever
/// stopped refusing, the gateway would be admitting on an answer that always says yes, and nothing
/// downstream would notice — so that assertion is the one this file exists for.</para>
/// <para><b>And role RESOLUTION is not enforcement.</b> Discovery filtering, state aliases,
/// <c>x-roles</c> field pruning and the caller-scoped caches all still need the caller's roles. Only
/// the 403 left.</para>
/// </remarks>
public sealed class EnforcementPostureTests : AuthorizationChainLabTestBase
{
    public EnforcementPostureTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// Every formerly gated read surface, addressed on the LEAF with a role the leaf's effective
    /// grants exclude (<c>chain.leaf-only</c> is replaced away by the mid's override). None of them
    /// may refuse any more.
    /// </summary>
    /// <remarks>
    /// The assertion is "not 403" rather than "200" on purpose: several of these legitimately answer
    /// 400 or 404 for their own reasons — <c>actions</c> needs a task that exists,
    /// <c>incidents/active</c> 404s when none is open — and conflating those with a refusal is exactly
    /// the mistake that would make this row prove nothing.
    /// </remarks>
    [Theory]
    [InlineData("functions/state")]
    [InlineData("functions/data")]
    [InlineData("functions/view")]
    [InlineData("functions/schema")]
    [InlineData("functions/master")]
    [InlineData("functions/tasks")]
    [InlineData("functions/actions?taskId=00000000-0000-0000-0000-000000000001")]
    [InlineData("incidents")]
    [InlineData("incidents/active")]
    public async Task NoReadSurfaceRefusesOnQueryRoles(string route)
    {
        var chain = await StartChainAsync();

        // `actions` carries a taskId because without one the request never reaches the gate — it is
        // rejected as a client error first, which would make this row prove nothing either way.
        var (status, _) = await CallInstanceRouteAsync(Leaf, chain.LeafId, route, "chain.leaf-only");

        Assert.NotEqual(HttpStatusCode.Forbidden, status);
    }

    /// <summary>
    /// The tenth surface: the acknowledge endpoint, the only state-changing one. It no longer
    /// refuses either.
    /// </summary>
    /// <remarks>
    /// <para>Its pre-flight, <c>authorize?ack=true</c>, remains and still admits through the very
    /// same <c>ILongPollInteractionGate</c> — which is what makes handing this decision to the
    /// gateway possible at all, because the interaction's <c>rule</c> arm is a C# script no gateway
    /// can evaluate itself. The gate is still in service; only its caller changed.</para>
    /// <para>The assertion is "not 403" rather than a fixed status because whether the leaf is still
    /// awaiting an acknowledgement when the call lands depends on the fallback timer. Measured while
    /// writing this: the leaf reported <c>status: A</c> and the endpoint answered <c>200</c>
    /// idempotently.</para>
    /// </remarks>
    [Fact]
    public async Task TheAcknowledgeEndpointDoesNotRefuse()
    {
        var chain = await StartChainAsync();

        var (status, _) = await SendRawAsync(
            HttpMethod.Post,
            $"api/v1/core/workflows/{Leaf}/instances/{chain.LeafId}/longpoll/ack",
            headers: Headers(LeafAdmin));

        Assert.NotEqual(HttpStatusCode.Forbidden, status);
    }

    /// <summary>
    /// <c>authorize</c> keeps refusing — it is the answer the Internal Gateway admits on, not a gate
    /// on this runtime's own path. If the removal had reached it, the gateway would be consulting an
    /// oracle that says yes to everything, and nothing downstream would notice.
    /// </summary>
    [Fact]
    public async Task AuthorizeKeepsRefusing()
    {
        var chain = await StartChainAsync();

        Assert.False(await IsAuthorizedAsync(Leaf, chain.LeafId, "chain.leaf-only", queryRoles: true),
            "authorize is the oracle, not a gate — the enforcement switch must not touch it");
    }

    /// <summary>
    /// Role RESOLUTION must survive the removal. This is the regression the change is most likely to
    /// cause: deleting the gate and the role lookup together would leave discovery filtering, x-roles
    /// pruning and the caller-scoped caches silently unkeyed.
    /// </summary>
    [Fact]
    public async Task DiscoveryFilteringStillDependsOnTheCallersRoles()
    {
        var chain = await StartChainAsync();

        var (adminStatus, adminBody) = await CallFunctionAsync(Root, chain.RootId, "state", Admin);
        Assert.Equal(HttpStatusCode.OK, adminStatus);

        var adminKeys = TransitionKeys(adminBody);
        Assert.Contains("record-note", adminKeys);

        // `record-note` grants chain.reader and chain.admin only. A caller outside that set must not
        // be offered it — this is visibility, which the runtime still owns; enforcement is what left.
        var (otherStatus, otherBody) = await CallFunctionAsync(Root, chain.RootId, "state", LeafAdmin);

        if (otherStatus == HttpStatusCode.OK)
            Assert.DoesNotContain("record-note", TransitionKeys(otherBody));
    }

    private static IReadOnlyList<string> TransitionKeys(System.Text.Json.JsonElement body)
    {
        var keys = new List<string>();
        if (body.ValueKind != System.Text.Json.JsonValueKind.Object) return keys;
        if (!body.TryGetProperty("transitions", out var transitions)) return keys;

        foreach (var transition in transitions.EnumerateArray())
        {
            if (transition.TryGetProperty("kind", out var kind) && kind.GetString() == "scheduled")
                continue;
            if (transition.TryGetProperty("name", out var name) && name.GetString() is { } key)
                keys.Add(key);
        }

        return keys;
    }
}
