using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AccountOpening;

/// <summary>
/// account-opening: a wizard-shaped flow with four product branches, a shared summary step and
/// two auto-evaluated gates.
/// <para>
/// account-type-selection → select-{product} → {product}-info (Wizard) → submit → account-summary
/// → approve-account-opening → policy-validation (auto) → account-creation (auto) →
/// account-opening-success
/// </para>
/// <para>
/// Both gates can send the application BACK to account-type-selection (<c>policies-failed</c>,
/// <c>account-creation-failed</c>), so "the flow moved on" is not the same as "the flow
/// succeeded" — the terminal assertions below name the success state explicitly.
/// </para>
/// <para>
/// <b>Was known red; green since account-opening v1.0.2 (2026-08-24).</b> The gap this class used
/// to signal was not in the entry tasks themselves: <c>UserSessionMapping</c> read
/// <c>context.Headers?["x-forwarded-for"]</c>, and that indexer throws on a missing key instead of
/// yielding null, so the start task's output handler died and <c>userSession</c> was never written.
/// Two mappings downstream then read the absent section dynamically — one of them swallowing the
/// throw and sending an empty request body, which the mock answered with 400 and the flow read as
/// <c>account-creation-failed</c>. All three mappings now read headers and instance data through
/// null-returning helpers. Should this class go red again, check the start task's log line first:
/// its failure is tolerated ("no ErrorBoundary is defined"), so the instance looks healthy.
/// </para>
/// </summary>
public class AccountOpeningTests : WorkflowTestBase
{
    private const string Workflow = "account-opening";
    private const string Roles = "morph-core.editor,morph-core.maker";

    public AccountOpeningTests(VNextTestEnvironment environment) : base(environment) { }

    private static object StartPayload() => new
    {
        session = $"S-{Guid.NewGuid():N}"[..12],
        customer = new { ownerUserId = "integration-test-user" },
    };

    /// <summary>Branch code must be four digits — the summary step rejects anything shorter.</summary>
    private static object DemandDepositInfo() => new
    {
        accountName = "Integration Test Account",
        currency = "TRY",
        branchCode = "0001",
    };

    private async Task<string> StartAtTypeSelectionAsync()
    {
        var id = await StartAsync(Workflow, StartPayload(), Roles);
        await WaitForInstanceStateAsync(Workflow, id, "account-type-selection", Roles);

        // account-type-selection runs entry tasks (notify-state, set-or-get-cache), so the
        // instance reaches this state whether or not they succeeded. Without this check a broken
        // start is only discovered later, as an unexplained "transition was refused".
        await AssertNotFaultedAsync(Workflow, id, Roles);
        return id;
    }

    [Fact]
    public async Task HappyPath_OpensADemandDepositAccount()
    {
        var id = await StartAtTypeSelectionAsync();

        await RunAcceptedAsync(Workflow, id, "select-demand-deposit",
            new { accountType = "demand-deposit" }, Roles);
        await WaitForInstanceStateAsync(Workflow, id, "demand-deposit-info", Roles);

        await RunAcceptedAsync(Workflow, id, "submit-demand-deposit-info", DemandDepositInfo(), Roles);
        await WaitForInstanceStateAsync(Workflow, id, "account-summary", Roles);

        await RunAcceptedAsync(Workflow, id, "approve-account-opening",
            new { confirmed = true, termsAccepted = true }, Roles);

        // policy-validation and account-creation are auto-evaluated; the flow walks itself from
        // here to a terminal state. A failing gate would park it back at account-type-selection.
        await WaitUntilAsync(
            async () => (await GetInstanceStateAsync(Workflow, id, Roles)).State == "account-opening-success",
            $"the application never reached account-opening-success — {await DescribeAsync(Workflow, id, Roles)}",
            TimeSpan.FromSeconds(90));

        var (_, status) = await GetInstanceStateAsync(Workflow, id, Roles);
        Assert.Equal("C", status);
    }

    [Fact]
    public async Task SelectingAProduct_RoutesToThatProductsWizardStep()
    {
        // Four transitions leave the same state; each must land on its own info step.
        var id = await StartAtTypeSelectionAsync();

        await RunAcceptedAsync(Workflow, id, "select-time-deposit",
            new { accountType = "time-deposit" }, Roles);

        await WaitForInstanceStateAsync(Workflow, id, "time-deposit-info", Roles);
    }

    [Fact]
    public async Task SubmitInfo_RejectsAnInvalidBranchCode()
    {
        var id = await StartAtTypeSelectionAsync();
        await RunAcceptedAsync(Workflow, id, "select-demand-deposit",
            new { accountType = "demand-deposit" }, Roles);
        await WaitForInstanceStateAsync(Workflow, id, "demand-deposit-info", Roles);

        var status = await RunAsync(Workflow, id, "submit-demand-deposit-info",
            new { accountName = "X", currency = "TRY", branchCode = "1" }, Roles);

        Assert.Equal(HttpStatusCode.BadRequest, status);

        // A rejected transition must leave the instance exactly where it was — schema validation
        // runs before admission, so nothing was flipped to Busy and nothing has to be released.
        var (state, instanceStatus) = await GetInstanceStateAsync(Workflow, id, Roles);
        Assert.Equal("demand-deposit-info", state);
        Assert.Equal("A", instanceStatus);
    }

    [Fact]
    public async Task WithoutACallerRole_TheStateFunctionIsForbidden()
    {
        // Discovery surfaces are role-gated. The transition endpoint deliberately is not — roles
        // describe what a client should offer, not a capability boundary.
        var id = await StartAtTypeSelectionAsync();

        var headers = Headers();
        headers.Remove("x-roles");
        headers.Remove("role");

        var (status, _) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{id}/functions/state", headers: headers);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    // NOTE — there is deliberately no "starting without x-device-id faults the instance" test.
    // Such a test passes today for the wrong reason: this flow's start faults in this environment
    // WITH the header too (see the class summary), so asserting "faulted" proves nothing about the
    // header. Add it back once the entry tasks run cleanly and a header-less start is genuinely
    // distinguishable from a normal one.

    [Fact]
    public async Task Cancel_MovesTheApplicationToCancelled()
    {
        var id = await StartAtTypeSelectionAsync();

        await RunAcceptedAsync(Workflow, id, "cancel-account-opening", roles: Roles);
        await WaitForInstanceStateAsync(Workflow, id, "cancelled", Roles, TimeSpan.FromSeconds(60));

        var (_, status) = await GetInstanceStateAsync(Workflow, id, Roles);
        Assert.True(TerminalStatuses.Contains(status), $"expected a terminal status, got {status}");
    }

    [Fact]
    public async Task Exit_AlsoLandsOnCancelled()
    {
        // cancel and exit are separate well-known transitions that share a target here; both must
        // be offered and both must terminate the application.
        var id = await StartAtTypeSelectionAsync();

        await RunAcceptedAsync(Workflow, id, "exit-account-opening", roles: Roles);
        await WaitForInstanceStateAsync(Workflow, id, "cancelled", Roles, TimeSpan.FromSeconds(60));

        var (_, status) = await GetInstanceStateAsync(Workflow, id, Roles);
        Assert.True(TerminalStatuses.Contains(status), $"expected a terminal status, got {status}");
    }
}
