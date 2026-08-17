using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.MoneyTransfer;

/// <summary>
/// money-transfer: a single-flow process that exercises rule-driven branching, a scheduled
/// (timer) transition and a terminal HTTP task.
/// <para>
/// enter-transfer-details → submit-details → review-and-confirm → confirm →
/// evaluate-push-requirement (auto, rule picks the branch) → awaiting-push-approval →
/// approve-push → executing-transfer (auto) → transfer-completed
/// </para>
/// </summary>
public class MoneyTransferTests : WorkflowTestBase
{
    private const string Workflow = "money-transfer";
    private const string Roles = "morph-core.maker";

    public MoneyTransferTests(VNextTestEnvironment environment) : base(environment) { }

    private static object TransferDetails(decimal amount = 100) => new
    {
        sourceAccountId = "ACC-1",
        targetIban = "TR330006100519786457841326",
        amount,
        currency = "TRY",
    };

    private async Task<string> StartAtConfirmAsync(decimal amount = 100)
    {
        var id = await StartAsync(Workflow, new { }, Roles);
        await WaitForInstanceStateAsync(Workflow, id, "enter-transfer-details", Roles);

        Assert.Equal(HttpStatusCode.Accepted,
            await RunAndSettleAsync(Workflow, id, "submit-details", TransferDetails(amount), Roles));
        await WaitForInstanceStateAsync(Workflow, id, "review-and-confirm", Roles);

        return id;
    }

    [Fact]
    public async Task HappyPath_ReachesTransferCompleted()
    {
        var id = await StartAtConfirmAsync();

        await RunAndSettleAsync(Workflow, id, "confirm", roles: Roles);
        await WaitForInstanceStateAsync(Workflow, id, "awaiting-push-approval", Roles);

        await RunAndSettleAsync(Workflow, id, "approve-push", roles: Roles);
        await WaitForInstanceStateAsync(Workflow, id, "transfer-completed", Roles, TimeSpan.FromSeconds(60));

        var (state, status) = await GetInstanceStateAsync(Workflow, id, Roles);
        Assert.Equal("transfer-completed", state);
        Assert.Equal("C", status);
    }

    [Fact]
    public async Task SubmitDetails_RejectsAPayloadThatViolatesTheTransitionSchema()
    {
        // The transition declares money-transfer-input; the runtime validates before accepting,
        // so a bad payload is a 400 at intake rather than a faulted instance later.
        var id = await StartAsync(Workflow, new { }, Roles);
        await WaitForInstanceStateAsync(Workflow, id, "enter-transfer-details", Roles);

        var status = await RunAsync(Workflow, id, "submit-details", new { sourceAccountId = "ACC-1" }, Roles);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("enter-transfer-details", (await GetInstanceStateAsync(Workflow, id, Roles)).State);
    }

    [Fact]
    public async Task AwaitingPushApproval_ArmsTheTimeoutTimer()
    {
        // push-timeout is a scheduled transition (triggerType 2) armed on entering the state.
        // It must show up as an armed entry the client can see, without being callable.
        var id = await StartAtConfirmAsync();

        await RunAndSettleAsync(Workflow, id, "confirm", roles: Roles);
        await WaitForInstanceStateAsync(Workflow, id, "awaiting-push-approval", Roles);

        var armedAt = await GetScheduledExecuteAtAsync(Workflow, id, "push-timeout", Roles);

        Assert.True(armedAt is not null,
            "push-timeout was not armed on entering awaiting-push-approval. Arming goes through " +
            "the Dapr Jobs API, so this also fails when the scheduler is unreachable — " +
            await DescribeAsync(Workflow, id, Roles));
    }

    [Fact]
    public async Task Cancel_MovesTheTransferToCancelled()
    {
        var id = await StartAtConfirmAsync();

        await RunAndSettleAsync(Workflow, id, "cancel-transfer", roles: Roles);
        await WaitForInstanceStateAsync(Workflow, id, "transfer-cancelled", Roles, TimeSpan.FromSeconds(60));

        var (state, status) = await GetInstanceStateAsync(Workflow, id, Roles);
        Assert.Equal("transfer-cancelled", state);
        Assert.True(TerminalStatuses.Contains(status), $"expected a terminal status, got {status}");
    }

    [Fact]
    public async Task ExecutingTransfer_RecordsTheProvidersResultInInstanceData()
    {
        // The execution step calls an HTTP task; its response is projected into instance data,
        // which is what the auto branch (succeeded/failed) then reads.
        var id = await StartAtConfirmAsync();

        await RunAndSettleAsync(Workflow, id, "confirm", roles: Roles);
        await WaitForInstanceStateAsync(Workflow, id, "awaiting-push-approval", Roles);
        await RunAndSettleAsync(Workflow, id, "approve-push", roles: Roles);
        await WaitForInstanceStateAsync(Workflow, id, "transfer-completed", Roles, TimeSpan.FromSeconds(60));

        var attributes = await GetAttributesAsync(Workflow, id, Roles);

        Assert.True(attributes.TryGetProperty("transferResult", out var result),
            "the transfer result was not projected into instance data");
        Assert.True(result.TryGetProperty("success", out var success) && success.GetBoolean(),
            "the provider call did not report success");
    }
}
