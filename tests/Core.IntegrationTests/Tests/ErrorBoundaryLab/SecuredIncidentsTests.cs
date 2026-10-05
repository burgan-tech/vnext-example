using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// Who may read an instance's incidents.
/// </summary>
/// <remarks>
/// <para>
/// The incident history, the active-incident endpoint and the state function share ONE read rule —
/// the <c>queryRoles</c> of <c>zone-secured</c> — and that rule is answered by
/// <c>authorize?queryRoles=true</c>, the question the gateway asks before forwarding any read. The
/// runtime no longer enforces it on the read endpoints themselves (gate removed 2026-09-23, council
/// <c>2026-09-22-remove-execution-authorization</c>). Until 2026-10-05 the first test here still
/// expected three 403s from the runtime and was the suite's one ErrorBoundaryLab red.
/// </para>
/// <para>
/// What is still worth pinning: the three read surfaces agree with each other for the same caller,
/// and <c>authorize</c> refuses a role-less caller while admitting the viewer — so a gateway that
/// asks first never forwards the incident detail of a secured instance to someone it would not show
/// the state to.
/// </para>
/// <para>
/// <c>zone-secured</c> carries the grant, not the workflow root, so starting an instance and firing
/// the case stay open to everyone while only the reads of a faulted instance are role-bound.
/// </para>
/// </remarks>
public class SecuredIncidentsTests : ErrorBoundaryLabTestBase
{
    public SecuredIncidentsTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task WithoutTheRole_AuthorizeRefusesTheRead_AndNoReadSurfaceIsGatedByTheRuntime()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-secured", roles: ViewerRole);
        await WaitUntilFaultedAsync(Workflow, instanceId, ViewerRole);

        var authorizeUrl = $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/authorize?queryRoles=true";
        var (roleLess, roleLessBody) = await SendRawAsync(HttpMethod.Get, authorizeUrl, headers: Headers());
        var (viewer, _) = await SendRawAsync(HttpMethod.Get, authorizeUrl, headers: Headers(ViewerRole));

        Assert.Equal(HttpStatusCode.Forbidden, roleLess);
        Assert.Contains("\"allowed\":false", roleLessBody);
        Assert.Equal(HttpStatusCode.OK, viewer);

        // The three read surfaces answer the role-less caller alike — none of them is a second,
        // divergent copy of the rule above.
        var (incidentStatus, _) = await GetIncidentsAsync(Workflow, instanceId);
        var (stateStatus, _, _) = await GetStateAsync(Workflow, instanceId);
        var (activeStatus, active) = await GetActiveIncidentAsync(Workflow, instanceId);

        Assert.Equal(HttpStatusCode.OK, incidentStatus);
        Assert.Equal(HttpStatusCode.OK, stateStatus);
        Assert.Equal(HttpStatusCode.OK, activeStatus);
        Assert.Equal("zone-secured", Text(active, "state"));
    }

    [Fact]
    public async Task WithTheRole_TheIncidentHistoryAnswers()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-secured", roles: ViewerRole);
        await WaitUntilFaultedAsync(Workflow, instanceId, ViewerRole);

        var (status, body) = await GetIncidentsAsync(Workflow, instanceId, ViewerRole);
        Assert.Equal(HttpStatusCode.OK, status);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.Equal("zone-secured", Text(items[^1], "state"));

        var (stateStatus, _, _) = await GetStateAsync(Workflow, instanceId, ViewerRole);
        Assert.Equal(HttpStatusCode.OK, stateStatus);

        var (activeStatus, active) = await GetActiveIncidentAsync(Workflow, instanceId, ViewerRole);
        Assert.Equal(HttpStatusCode.OK, activeStatus);
        Assert.Equal("zone-secured", Text(active, "state"));
    }
}
