using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// Workflow-scoped <c>authorize?type=instance</c> (vnext#1203): may this caller START an instance of the
/// flow? Answered before any instance exists, from the flow's own <c>startTransition.roles</c>, so a
/// gateway can gate <c>POST .../instances/start</c> the way it gates transitions.
/// </summary>
/// <remarks>
/// <para>Three sibling flows, one start-grant set each (start grants are per flow):</para>
/// <list type="bullet">
///   <item><c>role-matrix-lab-start-open</c> — no roles: everyone may start.</item>
///   <item><c>role-matrix-lab-start-gated</c> — <c>allow maker</c>, <c>deny viewer</c>, plus instance-bound
///   grants (<c>allow $InstanceStarter</c>, <c>deny $PreviousUser</c>, <c>deny $user.$.context.Instance.Data.blockedUserId</c>)
///   that have nothing to resolve against before a start and therefore never match.</item>
///   <item><c>role-matrix-lab-start-starter-only</c> — only <c>allow $InstanceStarter</c>: closed to everyone
///   (fail-closed), even to a caller carrying an actor identity.</item>
/// </list>
/// <para>URLs are built from <see cref="TestDomain.Name"/>, so the class also runs against a side domain.</para>
/// </remarks>
public sealed class StartAuthorizeTests : RoleMatrixLabTestBase
{
    public StartAuthorizeTests(VNextTestEnvironment environment) : base(environment) { }

    private const string Open = "role-matrix-lab-start-open";
    private const string Gated = "role-matrix-lab-start-gated";
    private const string StarterOnly = "role-matrix-lab-start-starter-only";

    private Task<(HttpStatusCode Status, string Body)> AuthorizeStartAsync(
        string flow, string? roles, string query = "type=instance", string? actSub = null) =>
        SendRawAsync(
            HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{flow}/functions/authorize" + (query.Length == 0 ? "" : "?" + query),
            headers: HeadersFor(roles, actSub));

    private async Task<bool> MayStartAsync(string flow, string? roles, string? actSub = null)
    {
        var (status, body) = await AuthorizeStartAsync(flow, roles, actSub: actSub);
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Forbidden,
            $"authorize?type=instance answered {(int)status}: {body}");

        // The verdict is in the body on both statuses.
        var allowed = JsonDocument.Parse(body).RootElement.GetProperty("allowed").GetBoolean();
        Assert.Equal(status == HttpStatusCode.OK, allowed);
        return allowed;
    }

    [Fact]
    public async Task NoStartRoles_EveryCallerMayStart_IncludingARoleLessOne()
    {
        Assert.True(await MayStartAsync(Open, Maker));
        Assert.True(await MayStartAsync(Open, Viewer));
        Assert.True(await MayStartAsync(Open, NoRole));
    }

    [Fact]
    public async Task StaticGrants_DecideAndDenyWins()
    {
        Assert.True(await MayStartAsync(Gated, Maker));
        Assert.False(await MayStartAsync(Gated, Approver));
        Assert.False(await MayStartAsync(Gated, Viewer));
        // A denied role is not bought back by an allowed one.
        Assert.False(await MayStartAsync(Gated, $"{Maker},{Viewer}"));
        Assert.False(await MayStartAsync(Gated, NoRole));
    }

    /// <summary>
    /// The instance-bound grants on the gated flow neither admit nor refuse: the approver carrying an
    /// actor identity is still refused (allow $InstanceStarter does not match), and the maker is still
    /// admitted (deny $PreviousUser / deny $user.… do not fire).
    /// </summary>
    [Fact]
    public async Task InstanceBoundGrants_NeverMatchBeforeAStart()
    {
        Assert.False(await MayStartAsync(Gated, Approver, actSub: StarterActor));
        Assert.True(await MayStartAsync(Gated, Maker, actSub: StarterActor));
    }

    [Fact]
    public async Task StarterOnlyStart_IsClosedToEveryone()
    {
        Assert.False(await MayStartAsync(StarterOnly, Maker, actSub: StarterActor));
        Assert.False(await MayStartAsync(StarterOnly, NoRole, actSub: StarterActor));
    }

    [Fact]
    public async Task PinnedVersion_IsHonoured_AndAnUnknownOneIsNotFound()
    {
        var (ok, _) = await AuthorizeStartAsync(Gated, Maker, "type=instance&version=1.0.0");
        Assert.Equal(HttpStatusCode.OK, ok);

        var (missing, _) = await AuthorizeStartAsync(Gated, Maker, "type=instance&version=9.9.9");
        Assert.Equal(HttpStatusCode.NotFound, missing);
    }

    /// <summary>
    /// <c>type</c> is required and has no default; an unknown value (including another casing) is
    /// refused the same way. Both answer <c>Authorization:110005</c> naming the supported values.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("type=transition")]
    [InlineData("type=Instance")]
    [InlineData("start=true")]
    public async Task MissingOrUnsupportedType_IsRejected(string query)
    {
        var (status, body) = await AuthorizeStartAsync(Gated, Maker, query);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Authorization:110005", body);
        Assert.Contains("Supported: instance", body);
    }

    [Fact]
    public async Task UnknownWorkflow_IsNotFound()
    {
        var (status, _) = await AuthorizeStartAsync("role-matrix-lab-start-missing", Maker);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    /// <summary>
    /// The runtime itself does not enforce <c>startTransition.roles</c> — the gateway decides on the
    /// answer above. A caller the oracle refuses can still start when it reaches the runtime directly.
    /// Pinned so that a change in this posture is a deliberate one.
    /// </summary>
    [Fact]
    public async Task RuntimeDoesNotEnforceStartRoles_TheGatewayDoes()
    {
        Assert.False(await MayStartAsync(Gated, Approver));

        var instanceId = await StartAsync(Gated, new { }, Approver);
        await WaitForInstanceStateAsync(Gated, instanceId, "waiting", Approver);
    }
}
