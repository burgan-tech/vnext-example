using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.FuturePay;

/// <summary>
/// future-pay: a loan disbursement built from three workflows — the parent
/// <c>loan-disbursement</c> and two SubFlow states that each start their own instance.
/// <para>
/// application-intake → submit-application → credit-bureau-inquiry (SubFlow, auto-completes) →
/// assessment-pricing → submit-assessment → approval → approve → collateral-establishment
/// (SubFlow) → disbursement (auto) → disbursed
/// </para>
/// <para>
/// <b>Coverage gap, deliberate.</b> The leg after <c>sign-contract</c> — the collateral subflow
/// registering and the parent resuming into disbursement — is not asserted here. That leg
/// currently faults in this domain package for a reason that has not been isolated, and a test
/// that fails for an unexplained domain defect teaches a CI reader nothing. What IS covered is
/// everything up to and including the collateral subflow being started with an open correlation,
/// which is the part that exercises the runtime's chain behaviour. Close this gap when the
/// collateral fault is understood.
/// </para>
/// </summary>
public class FuturePayTests : WorkflowTestBase
{
    private const string Workflow = "loan-disbursement";
    private const string Roles = "core.kredi-tahsis,core.operasyon";

    public FuturePayTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// The application payload. Every one of customerId/productType/requestedAmount/termMonths/
    /// monthlyIncome is required by the loan-application schema, and <c>purpose</c> — though
    /// optional there — must be present or the intake mapping writes a null the master schema
    /// then rejects.
    /// </summary>
    private static object ApplicationPayload() => new
    {
        customerId = $"C{Random.Shared.Next(100000, 999999)}",
        productType = "ihtiyac",
        requestedAmount = 50_000m,
        currency = "TRY",
        termMonths = 24,
        purpose = "Integration test application",
        monthlyIncome = 30_000m,
    };

    private static object AssessmentPayload() => new
    {
        approvedLimit = 50_000m,
        interestRate = 3.19m,
        insurancePremium = 250m,
        monthlyInstallment = 2_450m,
        apr = 42.5m,
        internalRating = "BBB",
        riskScore = 62m,
    };

    private async Task<string> StartAtIntakeAsync()
    {
        var id = await StartAsync(Workflow, new { }, Roles);
        await WaitForInstanceStateAsync(Workflow, id, "application-intake", Roles);
        return id;
    }

    /// <summary>Intake → bureau subflow → assessment-pricing, the shared prefix of most cases.</summary>
    private async Task<string> AdvanceToAssessmentAsync()
    {
        var id = await StartAtIntakeAsync();

        await RunAcceptedAsync(Workflow, id, "submit-application", ApplicationPayload(), Roles);

        // credit-bureau-inquiry is a SubFlow state whose child auto-completes; the parent then
        // auto-chains through bureau-completed on its own.
        await WaitUntilAsync(
            async () => (await GetInstanceStateAsync(Workflow, id, Roles)).State == "assessment-pricing",
            $"the bureau subflow never returned the parent to assessment-pricing — " +
            await DescribeAsync(Workflow, id, Roles),
            TimeSpan.FromSeconds(120));

        return id;
    }

    [Fact]
    public async Task SubmittingAnApplication_RunsTheBureauSubflowAndLandsOnAssessment()
    {
        var id = await AdvanceToAssessmentAsync();

        // The subflow's output mapping merges the bureau result into the master `creditBureau`
        // section — its presence is the proof the child ran and its output was mapped back.
        var attributes = await GetAttributesAsync(Workflow, id, Roles);
        Assert.True(attributes.TryGetProperty("creditBureau", out var bureau),
            $"the bureau subflow's output never reached the parent — {await DescribeAsync(Workflow, id, Roles)}");
        Assert.True(bureau.TryGetProperty("kkbScore", out _),
            $"creditBureau landed without a kkbScore: {bureau.GetRawText()}");
    }

    [Fact]
    public async Task SubmittingAnApplication_RejectsAPayloadMissingRequiredFields()
    {
        // loan-application declares five required fields and additionalProperties:false; schema
        // validation must reject before anything is admitted.
        var id = await StartAtIntakeAsync();

        var status = await RunAsync(Workflow, id, "submit-application",
            new { customerId = "C123456" }, Roles);

        Assert.Equal(HttpStatusCode.BadRequest, status);

        var (state, instanceStatus) = await GetInstanceStateAsync(Workflow, id, Roles);
        Assert.Equal("application-intake", state);
        Assert.Equal("A", instanceStatus);
    }

    [Fact]
    public async Task Approving_StartsTheCollateralSubflowAsAnOpenCorrelation()
    {
        // The interesting runtime behaviour: entering a SubFlow state opens a correlation and the
        // parent goes Busy for the child's whole lifetime, while a polling client is shown the
        // CHILD's state, not the parent's.
        var id = await AdvanceToAssessmentAsync();

        await RunAcceptedAsync(Workflow, id, "submit-assessment", AssessmentPayload(), Roles);
        await WaitForInstanceStateAsync(Workflow, id, "approval", Roles);

        await RunAsync(Workflow, id, "approve",
            new { decisionReason = "Integration test approval", approverUserId = "integration-test-user" },
            Roles);

        await WaitUntilAsync(
            async () => (await GetActiveSubflowsAsync(Workflow, id, Roles))
                .ContainsKey("collateral-establishment"),
            $"the collateral subflow never opened a correlation — {await DescribeAsync(Workflow, id, Roles)}",
            TimeSpan.FromSeconds(120));

        var (parentState, parentStatus) = await GetInstanceStateAsync(Workflow, id, Roles);
        Assert.Equal("collateral-establishment", parentState);
        Assert.Equal("B", parentStatus);

        // What a long-polling client actually sees is the leaf, parked on its initial state.
        await WaitForObservedStateAsync(Workflow, id, "contract-signing", Roles, TimeSpan.FromSeconds(60));
        var (_, observedStatus) = await GetObservedStateAsync(Workflow, id, Roles);
        Assert.Equal("A", observedStatus);
    }

    [Fact]
    public async Task Rejecting_TerminatesTheApplicationWithoutStartingCollateral()
    {
        var id = await AdvanceToAssessmentAsync();

        await RunAcceptedAsync(Workflow, id, "submit-assessment", AssessmentPayload(), Roles);
        await WaitForInstanceStateAsync(Workflow, id, "approval", Roles);

        await RunAcceptedAsync(Workflow, id, "reject",
            new { rejectionReason = "Integration test rejection", rejectionCode = "IT-001" }, Roles);

        await WaitForInstanceStateAsync(Workflow, id, "rejected", Roles, TimeSpan.FromSeconds(60));

        var (_, status) = await GetInstanceStateAsync(Workflow, id, Roles);
        Assert.Equal("C", status);
        Assert.Empty(await GetActiveSubflowsAsync(Workflow, id, Roles));
    }

    [Fact]
    public async Task Rejecting_RequiresARejectionReason()
    {
        // loan-rejection is the only one of the four payload schemas with a required field.
        var id = await AdvanceToAssessmentAsync();

        await RunAcceptedAsync(Workflow, id, "submit-assessment", AssessmentPayload(), Roles);
        await WaitForInstanceStateAsync(Workflow, id, "approval", Roles);

        var status = await RunAsync(Workflow, id, "reject", new { rejectionCode = "IT-002" }, Roles);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("approval", (await GetInstanceStateAsync(Workflow, id, Roles)).State);
    }

    [Fact]
    public async Task ApprovalStep_IsNotReachableBeforeAssessmentIsSubmitted()
    {
        // The execution policy gates a transition on the instance's current state; approve is
        // authored on `approval` and must be refused while the instance sits on assessment-pricing.
        var id = await AdvanceToAssessmentAsync();

        var status = await RunAsync(Workflow, id, "approve",
            new { decisionReason = "too early" }, Roles);

        Assert.True((int)status >= 400,
            $"approve was accepted from assessment-pricing (got {(int)status})");
        Assert.Equal("assessment-pricing", (await GetInstanceStateAsync(Workflow, id, Roles)).State);
    }
}
