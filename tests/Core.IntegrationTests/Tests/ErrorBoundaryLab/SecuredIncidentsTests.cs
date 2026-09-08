using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// Who may read an instance's incidents.
/// </summary>
/// <remarks>
/// <para>
/// The incident history AND the active-incident endpoint are gated by exactly the same
/// <c>queryRoles</c> check as the state function:
/// a caller who may poll the state may read why it stalled, and a caller who may not gets 403 from
/// both. That symmetry is the property under test — an incident history readable by callers the
/// state function refuses would leak the failure detail the gate exists to protect.
/// </para>
/// <para>
/// <c>zone-secured</c> carries the grant, not the workflow root, so starting an instance and firing
/// the case stay open to everyone while every READ of a faulted instance is gated.
/// </para>
/// </remarks>
public class SecuredIncidentsTests : ErrorBoundaryLabTestBase
{
    public SecuredIncidentsTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task WithoutTheRole_BothTheStateFunctionAndTheIncidentHistoryRefuse()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-secured", roles: ViewerRole);
        await WaitUntilFaultedAsync(Workflow, instanceId, ViewerRole);

        var (incidentStatus, incidentBody) = await GetIncidentsAsync(Workflow, instanceId);
        var (stateStatus, _, _) = await GetStateAsync(Workflow, instanceId);
        var (activeStatus, _) = await GetActiveIncidentAsync(Workflow, instanceId);

        Assert.Equal(HttpStatusCode.Forbidden, incidentStatus);
        Assert.Equal(HttpStatusCode.Forbidden, stateStatus);

        // The active-incident endpoint is the third surface behind the same gate. It must refuse a
        // roleless caller rather than answer 404, or "no incident" and "not allowed to know" become
        // indistinguishable and the gate leaks by omission.
        Assert.Equal(HttpStatusCode.Forbidden, activeStatus);
        Assert.Contains("Authorization:110001", incidentBody.ToString());
        Assert.Contains("zone-secured", incidentBody.ToString());
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
