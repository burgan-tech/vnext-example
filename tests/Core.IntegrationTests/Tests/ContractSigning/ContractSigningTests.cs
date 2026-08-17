using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ContractSigning;

/// <summary>
/// contract-signing: three cooperating flows wired by tasks rather than by SubFlow states.
/// <para>
/// <c>login-flow</c> (the root) starts <c>contract-flow</c> as a SubProcess. contract-flow reads
/// the document list, then spawns one <c>online-flow</c> per document through a <c>$self</c> auto
/// loop. Each online-flow is approved individually; the approvals travel back up as triggered
/// transitions until login-flow can be finalised, at which point every instance is Completed.
/// </para>
/// <para>
/// Nothing here is a SubFlow correlation, so the chain is followed through instance DATA:
/// login-flow records <c>contractInstanceId</c>, contract-flow records <c>onlineInstanceIds</c>.
/// </para>
/// </summary>
public class ContractSigningTests : WorkflowTestBase
{
    private const string LoginFlow = "login-flow";
    private const string ContractFlow = "contract-flow";
    private const string OnlineFlow = "online-flow";

    public ContractSigningTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// The start payload carries the caller's identity claims. The SubProcess start mapping reads
    /// them without a guard, so omitting them faults the instance before anything else happens.
    /// </summary>
    private static object StartPayload() => new
    {
        contractCode = $"CT-{Guid.NewGuid():N}"[..10],
        sub = "integration-test-user",
        act_sub = "integration-test-user",
    };

    private async Task<string> ReadStringAsync(string workflow, string instanceId, string property)
    {
        var attributes = await GetAttributesAsync(workflow, instanceId);
        return attributes.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
    }

    private async Task<string> WaitForContractInstanceAsync(string loginId)
    {
        var contractId = "";
        await WaitUntilAsync(async () =>
        {
            contractId = await ReadStringAsync(LoginFlow, loginId, "contractInstanceId");
            return !string.IsNullOrEmpty(contractId);
        }, "login-flow never recorded the contract instance it started", TimeSpan.FromSeconds(60));

        return contractId;
    }

    /// <summary>
    /// Waits until the spawn loop has produced one instance per document. Reading the list while
    /// the $self loop is still iterating yields a partial set, so the document count is the
    /// completion signal.
    /// </summary>
    private async Task<List<string>> WaitForOnlineInstancesAsync(string contractId)
    {
        var online = new List<string>();
        await WaitUntilAsync(async () =>
        {
            var attributes = await GetAttributesAsync(ContractFlow, contractId);
            if (!attributes.TryGetProperty("documentCount", out var count) ||
                !attributes.TryGetProperty("onlineInstanceIds", out var ids) ||
                ids.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var expected = count.GetInt32();
            if (expected == 0 || ids.GetArrayLength() < expected) return false;

            online = ids.EnumerateArray().Select(x => x.GetString()!).ToList();
            return true;
        }, $"contract-flow never spawned one online instance per document — " +
           $"{await DescribeAsync(ContractFlow, contractId)}", TimeSpan.FromSeconds(60));

        return online;
    }

    /// <summary>Starts login-flow and drives it to the point where every document awaits approval.</summary>
    private async Task<(string LoginId, string ContractId, List<string> OnlineIds)> StartAndSpawnAsync()
    {
        var loginId = await StartAsync(LoginFlow, StartPayload());
        var contractId = await WaitForContractInstanceAsync(loginId);
        var onlineIds = await WaitForOnlineInstancesAsync(contractId);

        foreach (var onlineId in onlineIds)
        {
            await WaitForInstanceStateAsync(OnlineFlow, onlineId, "pre-approval-waiting", timeout: TimeSpan.FromSeconds(60));
        }

        // The approvals travel back up as triggered transitions, and login-flow can only accept
        // them once the `login-ready` trigger has moved it into the awaiting state. Approving
        // before that races the trigger and the approvals land nowhere.
        await WaitForInstanceStateAsync(LoginFlow, loginId, "waiting-approval-doc", timeout: TimeSpan.FromSeconds(60));

        return (loginId, contractId, onlineIds);
    }

    [Fact]
    public async Task StartingLoginFlow_SpawnsTheContractSubProcess_AndOneOnlineFlowPerDocument()
    {
        var (_, contractId, onlineIds) = await StartAndSpawnAsync();

        var attributes = await GetAttributesAsync(ContractFlow, contractId);
        var documentCount = attributes.GetProperty("documentCount").GetInt32();

        Assert.True(documentCount > 0, "the document list came back empty");
        Assert.Equal(documentCount, onlineIds.Count);
        Assert.Equal(onlineIds.Count, onlineIds.Distinct().Count());
    }

    [Fact]
    public async Task EachOnlineFlow_CarriesItsOwnDocument()
    {
        // The spawning loop is a $self auto loop: every iteration must produce a DISTINCT child,
        // not repeat one. Distinct document ids are the cheapest proof the loop advanced.
        var (_, _, onlineIds) = await StartAndSpawnAsync();

        var documentIds = new List<string>();
        foreach (var onlineId in onlineIds)
        {
            documentIds.Add(await ReadStringAsync(OnlineFlow, onlineId, "documentId"));
        }

        Assert.DoesNotContain("", documentIds);
        Assert.Equal(documentIds.Count, documentIds.Distinct().Count());
    }

    [Fact]
    public async Task ApprovingEveryDocument_MovesLoginFlowToAllDocumentsApproved()
    {
        var (loginId, _, onlineIds) = await StartAndSpawnAsync();

        foreach (var onlineId in onlineIds)
        {
            await RunAsync(OnlineFlow, onlineId, "pre-approve");
        }

        // Each approval is triggered back up as a transition; login-flow only advances once the
        // last one lands.
        await WaitForInstanceStateAsync(LoginFlow, loginId, "all-documents-approved", timeout: TimeSpan.FromSeconds(90));

        Assert.Equal("all-documents-approved", (await GetInstanceStateAsync(LoginFlow, loginId)).State);
    }

    [Fact]
    public async Task FinalisingLoginFlow_CompletesEveryInstanceInTheChain()
    {
        var (loginId, contractId, onlineIds) = await StartAndSpawnAsync();

        foreach (var onlineId in onlineIds)
        {
            await RunAsync(OnlineFlow, onlineId, "pre-approve");
        }

        await WaitForInstanceStateAsync(LoginFlow, loginId, "all-documents-approved", timeout: TimeSpan.FromSeconds(90));

        await RunAsync(LoginFlow, loginId, "login-finalize");

        await WaitUntilAsync(async () =>
            (await GetInstanceStateAsync(LoginFlow, loginId)).Status == "C",
            "login-flow never completed", TimeSpan.FromSeconds(120));

        await WaitUntilAsync(async () =>
            (await GetInstanceStateAsync(ContractFlow, contractId)).Status == "C",
            "contract-flow never completed", TimeSpan.FromSeconds(120));

        foreach (var onlineId in onlineIds)
        {
            await WaitUntilAsync(async () =>
                (await GetInstanceStateAsync(OnlineFlow, onlineId)).Status == "C",
                $"online-flow {onlineId} never completed", TimeSpan.FromSeconds(120));
        }

        Assert.Equal("login-completed-state", (await GetInstanceStateAsync(LoginFlow, loginId)).State);
        Assert.Equal("contract-completed", (await GetInstanceStateAsync(ContractFlow, contractId)).State);
    }

    [Fact]
    public async Task StartingWithoutIdentityClaims_FaultsTheInstance()
    {
        // Documents the sharp edge: the SubProcess start mapping reads `sub` from instance data
        // without a guard, so a payload that satisfies the transition schema can still fault the
        // instance on the very first task.
        var loginId = await StartAsync(LoginFlow, new { contractCode = $"CT-{Guid.NewGuid():N}"[..10] });

        await WaitUntilAsync(async () =>
            (await GetInstanceStateAsync(LoginFlow, loginId)).Status == "F",
            "expected the instance to fault without identity claims", TimeSpan.FromSeconds(45));

        Assert.Equal("login-initial", (await GetInstanceStateAsync(LoginFlow, loginId)).State);
    }
}
