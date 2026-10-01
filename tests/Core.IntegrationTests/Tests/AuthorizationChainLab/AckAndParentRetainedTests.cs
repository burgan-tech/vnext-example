using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AuthorizationChainLab;

/// <summary>
/// The <c>ack</c> target and the parent-retained transitions — the two places where
/// <c>authorize</c> must mirror a behaviour that lives somewhere else in the runtime.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <c>POST .../longpoll/ack</c> is the only state-changing surface in
/// the authorization change's scope, and the middle tier had no way to ask about it: <c>authorize</c>
/// took a transition key, a function key or <c>queryRoles</c>, none of which describe an
/// acknowledge. The new <c>?ack=true</c> target closes that, and it has to agree with the endpoint
/// exactly — a pre-flight that answers a different question is worse than none.</para>
/// <para>The parent-retention half is the mirror-image risk: with an active SubFlow, <c>cancel</c>,
/// <c>exit</c>, <c>updateData</c> and an in-state shared transition all execute on the PARENT
/// (<c>HandleCancelPreflightStep</c> at order 5 skips over the forward at order 10; the forward step
/// excludes the other two). <c>authorize</c> must answer against the parent for exactly those and
/// descend for everything else.</para>
/// </remarks>
public sealed class AckAndParentRetainedTests : AuthorizationChainLabTestBase
{
    public AckAndParentRetainedTests(VNextTestEnvironment environment) : base(environment) { }

    // ── ack ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pre-flight and the call must give the same verdict. The leaf's
    /// <c>interaction.longPoll.roles</c> names <c>chain.admin</c>; nothing else may acknowledge.
    /// </summary>
    /// <remarks>
    /// The assertion is AGREEMENT, not a fixed verdict. Whether the leaf is actually awaiting an
    /// acknowledgement at the moment of the call depends on the fallback timer, and a test that hard
    /// -coded "denied" would fail for the wrong reason once the pause has been resumed. What must hold
    /// in every case is that the pre-flight and the call answer the same thing.
    /// </remarks>
    [Theory]
    [InlineData(Admin)]
    [InlineData(LeafAdmin)]
    [InlineData(null)]
    public async Task AuthorizeAckAgreesWithTheAcknowledgeEndpoint(string? roles)
    {
        var chain = await StartChainAsync();

        var preflight = await IsAuthorizedAsync(Leaf, chain.LeafId, roles, ack: true);

        var (ackStatus, _) = await SendRawAsync(
            HttpMethod.Post,
            $"api/v1/core/workflows/{Leaf}/instances/{chain.LeafId}/longpoll/ack",
            headers: Headers(roles));

        // The endpoint answers 403 on refusal and 2xx on admission. Anything else (a 404 because
        // nothing is awaiting, say) means the fixture is not in the state this test assumes.
        var endpointAllowed = ackStatus switch
        {
            HttpStatusCode.Forbidden => false,
            _ when (int)ackStatus is >= 200 and < 300 => true,
            _ => throw new Xunit.Sdk.XunitException(
                $"the acknowledge endpoint answered {(int)ackStatus}; the pre-flight said " +
                $"{(preflight ? "allowed" : "denied")} and this test cannot compare the two")
        };

        Assert.Equal(preflight, endpointAllowed);
    }

    /// <summary>
    /// Asking about an instance where nothing is awaiting must answer ALLOWED, because that is what
    /// the endpoint does — it returns Ok() idempotently. A refusal here would have the middle tier
    /// 403 a call the runtime accepts.
    /// </summary>
    [Fact]
    public async Task AckIsAllowedWhenNothingIsAwaiting()
    {
        var chain = await StartChainAsync();

        // The root is parked on its SubFlow state; it is not the instance that paused for an ack.
        Assert.True(await IsAuthorizedAsync(Root, chain.RootId, Admin, ack: true),
            "nothing in the chain above the leaf is awaiting an acknowledgement, and the endpoint " +
            "answers Ok() there, so the pre-flight must say allowed");
    }

    /// <summary>Exactly one target is required — naming two is a client error, not a denial.</summary>
    [Fact]
    public async Task AckAndQueryRolesTogetherAreRejected()
    {
        var chain = await StartChainAsync();

        var (status, _) = await AuthorizeAsync(
            Root, chain.RootId, Admin, queryRoles: true, ack: true);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ── parent-retained transitions ──────────────────────────────────────────

    /// <summary>
    /// With an active SubFlow the parent retains these, so <c>authorize</c> must answer against the
    /// ROOT's definition. The leaf declares none of them; if the call descended, every one of these
    /// would be denied for want of a matching transition.
    /// </summary>
    [Theory]
    [InlineData("cancel-root")]
    [InlineData("exit-root")]
    [InlineData("update-root-data")]
    [InlineData("record-note")]
    public async Task ParentRetainedTransitionsAreAnsweredAgainstTheRoot(string transitionKey)
    {
        var chain = await StartChainAsync();

        Assert.True(await IsAuthorizedAsync(Root, chain.RootId, Admin, transitionKey: transitionKey),
            $"{transitionKey} executes on the parent while a SubFlow is active, so authorize must " +
            "answer from the root's definition rather than descending to a leaf that never declares it");
    }

    /// <summary>
    /// The other side of the same rule: a key the parent does NOT retain is forwarded, and the leaf's
    /// answer is the one that counts. <c>finish-leaf</c> exists only on the leaf.
    /// </summary>
    [Fact]
    public async Task ANonRetainedTransitionIsAnsweredByTheLevelThatOwnsIt()
    {
        var chain = await StartChainAsync();

        Assert.True(await IsAuthorizedAsync(Leaf, chain.LeafId, LeafAdmin, transitionKey: "finish-leaf"),
            "finish-leaf is the leaf's own transition and the leaf's grants admit chain.leaf-admin");
    }
}
