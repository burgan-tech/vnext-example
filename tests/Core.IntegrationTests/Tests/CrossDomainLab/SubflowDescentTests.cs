using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// AC-01..AC-06 of labs/cross-domain/VNEXT-BUILD-PLAN.md: a core parent whose SubFlow state runs in the
/// partner domain. Every read goes to the CORE parent and must be answered with PARTNER content —
/// that is the descent through <c>RemoteInstanceQueryAppService</c> over Dapr service invocation.
/// </summary>
[Collection("VNextIntegration")]
public class SubflowDescentTests : CrossDomainLabTestBase, IClassFixture<CrossDomainLabFixture>
{
    public SubflowDescentTests(VNextTestEnvironment environment, CrossDomainLabFixture lab) : base(environment, lab) { }

    /// <summary>AC-01: entering the SubFlow state starts xd-child in partner; the state function reports the leaf.</summary>
    [SkippableFact]
    public async Task EnterSubflow_StartsChildInPartner_AndStateDescends()
    {
        RequirePartner();
        var (parentId, testId) = await StartParentAsync("xd-enter");

        var childId = await EnterSubflowAsync(parentId);

        // child-approve is role-gated in the PARTNER child (xd-approver); the state function on the
        // core parent applies that filter too, so read it as the approver — a role-less read shows
        // only the well-known cancel entries.
        var (status, state) = await CallParentFunctionAsync(parentId, "state", Approver);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("child-review", Str(state, "state"));

        var correlation = state.GetProperty("activeCorrelations").EnumerateArray().First();
        Assert.Equal("partner", Str(correlation, "subFlowDomain"));
        Assert.Equal(childId, Str(correlation, "subFlowInstanceId"));

        // the child really lives in the partner runtime and got the parent's testId
        var (http, childState, _, attributes) = await GetPartnerInstanceAsync(Child, childId);
        Assert.Equal(HttpStatusCode.OK, http);
        Assert.Equal("child-review", childState);
        Assert.Equal(testId, Str(attributes, "testId"));
        Assert.Equal(parentId, Str(attributes, "parentInstanceId"));

        // and the parent's transitions come from the child: child-approve is offered, enter-subflow is not
        var transitions = state.GetProperty("transitions").EnumerateArray().Select(t => Str(t, "name")).ToList();
        Assert.Contains("child-approve", transitions);
        Assert.DoesNotContain("enter-subflow", transitions);
    }

    /// <summary>AC-02: the view function on the core parent answers with the partner-only view.</summary>
    [SkippableFact]
    public async Task ViewFunction_DescendsToPartner()
    {
        RequirePartner();
        var (parentId, _) = await StartParentAsync("xd-view");
        await EnterSubflowAsync(parentId);

        var (status, body) = await CallParentFunctionAsync(parentId, "view");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("xd-child-review-view", Str(body, "key"));
    }

    /// <summary>AC-03: the schema function for the child's transition is served from partner.</summary>
    [SkippableFact]
    public async Task SchemaFunction_DescendsToPartner()
    {
        RequirePartner();
        var (parentId, _) = await StartParentAsync("xd-schema");
        await EnterSubflowAsync(parentId);

        var (status, body) = await CallParentFunctionAsync(parentId, "schema", query: "transitionKey=child-approve");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("xd-child-approve", Str(body, "key"));
        var required = body.GetProperty("schema").GetProperty("required").EnumerateArray().Select(r => r.GetString());
        Assert.Contains("approvedBy", required);
    }

    /// <summary>
    /// AC-04: the data function keeps the PARENT's body (runtime decision, pinned here) while the
    /// requested extension is produced by the PARTNER child.
    /// </summary>
    [SkippableFact]
    public async Task DataFunction_ExtensionsDescend_BodyStaysParent()
    {
        RequirePartner();
        var (parentId, testId) = await StartParentAsync("xd-data");
        var childId = await EnterSubflowAsync(parentId);

        var (status, body) = await CallParentFunctionAsync(parentId, "data", query: "extensions=xd-child-ext");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(testId, Str(body.GetProperty("data"), "testId"));
        Assert.False(body.GetProperty("data").TryGetProperty("parentInstanceId", out _),
            "the data body carried the CHILD's fields — the runtime started descending the body, update AC-04");

        Assert.True(body.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object,
            $"no extensions object in the data response: {body}");
        var fromPartner = extensions.EnumerateObject()
            .Select(p => p.Value)
            .FirstOrDefault(v => v.ValueKind == JsonValueKind.Object && Str(v, "source") == "partner");
        Assert.True(fromPartner.ValueKind == JsonValueKind.Object, $"xd-child-ext payload not found in {extensions}");
        Assert.Equal(childId, Str(fromPartner, "childInstanceId"));
        Assert.Equal(testId, Str(fromPartner, "testId"));
    }

    /// <summary>AC-05: authorize on the core parent is decided by the PARTNER child's transition roles.</summary>
    [SkippableFact]
    public async Task Authorize_ForwardsToPartnerRoles()
    {
        RequirePartner();
        var (parentId, _) = await StartParentAsync("xd-auth");
        await EnterSubflowAsync(parentId);

        var (allowed, _) = await AuthorizeAsync(parentId, Approver, "child-approve");
        var (denied, _) = await AuthorizeAsync(parentId, Viewer, "child-approve");

        Assert.Equal(HttpStatusCode.OK, allowed);
        Assert.Equal(HttpStatusCode.Forbidden, denied);
    }

    /// <summary>AC-06: a transition sent to the core parent is forwarded to the partner child; the child completes and the parent resumes.</summary>
    [SkippableFact]
    public async Task ForwardTransition_CompletesChild_ParentResumes()
    {
        RequirePartner();
        var (parentId, _) = await StartParentAsync("xd-fwd");
        var childId = await EnterSubflowAsync(parentId);

        await ApproveChildThroughParentAsync(parentId, approvedBy: "xd-tester");

        var (http, childState, childStatus, childAttributes) = await GetPartnerInstanceAsync(Child, childId);
        Assert.Equal(HttpStatusCode.OK, http);
        Assert.Equal("child-completed", childState);
        Assert.Equal("C", childStatus);
        Assert.Equal("xd-tester", Str(childAttributes, "approvedBy"));

        var (parentState, parentStatus) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("xd-after-subflow", parentState);
        Assert.Equal("A", parentStatus);

        var attributes = await GetAttributesAsync(Parent, parentId);
        Assert.True(attributes.GetProperty("childCompleted").GetBoolean());
        Assert.Equal("xd-tester", Str(attributes, "childApprovedBy"));
    }
}
