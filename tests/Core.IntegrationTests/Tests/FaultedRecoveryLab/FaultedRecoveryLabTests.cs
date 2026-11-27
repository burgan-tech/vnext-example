using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.FaultedRecoveryLab;

/// <summary>
/// faulted-recovery-lab (vnext#1252): an instance that faulted because a transition's mapping threw, or
/// because the data that mapping produced failed the closed master schema, must be recoverable through
/// the API. <c>retry</c> re-runs the failed hop and the reserved <c>cancel</c> closes the instance.
/// </summary>
/// <remarks>
/// <para>
/// The mapping and the data write run at the start of the hop, before its transition record was saved.
/// Such a fault therefore left no unfinished record: <c>retry</c> answered <c>400 Instance:100028</c> ("no
/// incomplete transition"), and the reserved <c>cancel</c> answered <c>400 Instance:100018</c> because
/// Faulted counts as terminal. Only a database edit could recover the instance.
/// </para>
/// <para>
/// Every slot uses the same mapping (<c>FailOnDemandMapping.csx</c>): the request's <c>failWith</c> makes it
/// throw (<c>"throw"</c>) or return a field the master schema does not declare (<c>"schema"</c>).
/// </para>
/// </remarks>
public class FaultedRecoveryLabTests : WorkflowTestBase
{
    private const string Flow = "faulted-recovery-lab";
    private const string Parent = "faulted-recovery-lab-parent";
    private const string Child = "faulted-recovery-lab-child";
    private const string Restricted = "faulted-recovery-lab-restricted";
    private const string NoCancel = "faulted-recovery-lab-nocancel";
    private const string NotAvailableInState = "Transition:100024";
    private const string TerminalInstance = "Instance:100018";
    private const string MappingFailed = "Execution:200002";
    private const string DuplicateKey = "Instance:100019";

    public FaultedRecoveryLabTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task ManualMappingThrows_FaultsWithAnIncidentAndAnOpenRecord_AndRetryReRunsTheHop()
    {
        var id = await StartLabAsync();

        var (status, body) = await TransitionAsync(id, "fr-advance", new { failWith = "throw" });

        Assert.True(status == HttpStatusCode.OK, $"the failed hop answered {(int)status}: {body}");
        Assert.Equal("F", StatusOf(body));
        await AssertFaultedInAsync(id, "fr-draft");
        await AssertActiveIncidentAsync(id, "fr-advance", MappingFailed);
        await AssertOpenRecordAsync(id, "fr-advance");

        var (retryStatus, retryBody) = await RetryAsync(id, new { note = "fixed" });

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "fr-active", "A");
        await AssertNoActiveIncidentAsync(id);
        await AssertDataAsync(id, note: "fixed");
        await AssertNoOpenRecordAsync(id);
        Assert.Equal(1, await CountRecordsAsync(id, "fr-advance"));
    }

    [Fact]
    public async Task ManualMappingThrows_TheReservedCancelClosesTheFaultedInstance()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });
        await AssertFaultedInAsync(id, "fr-draft");

        var (status, body) = await TransitionAsync(id, "fr-cancel", new { note = "operator closed it" });

        Assert.True(status == HttpStatusCode.OK, $"cancel answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-cancelled", "C");
        await AssertNoActiveIncidentAsync(id);
        await AssertNoOpenRecordAsync(id);
    }

    [Fact]
    public async Task UpdateDataMappingThrows_AnswersFaulted_AndRetryReappliesTheUpdate()
    {
        var id = await StartLabAsync();

        var (status, body) = await TransitionAsync(id, "fr-update", new { failWith = "throw", note = "lost" });

        Assert.True(status == HttpStatusCode.OK, $"the failed update answered {(int)status}: {body}");
        Assert.Equal("F", StatusOf(body));
        await AssertFaultedInAsync(id, "fr-draft");
        await AssertActiveIncidentAsync(id, "fr-update", MappingFailed);
        await AssertOpenRecordAsync(id, "fr-update");

        var (retryStatus, retryBody) = await RetryAsync(id, new { note = "re-applied" });

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "fr-draft", "A");
        await AssertNoActiveIncidentAsync(id);
        await AssertDataAsync(id, note: "re-applied");
        await AssertNoOpenRecordAsync(id);
    }

    [Fact]
    public async Task UpdateDataMappingThrows_TheReservedCancelClosesTheFaultedInstance()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-update", new { failWith = "throw" });
        await AssertFaultedInAsync(id, "fr-draft");

        var (status, body) = await TransitionAsync(id, "fr-cancel", new { });

        Assert.True(status == HttpStatusCode.OK, $"cancel answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-cancelled", "C");
        await AssertNoActiveIncidentAsync(id);
    }

    [Fact]
    public async Task CancelMappingThrows_AnswersFaulted_AndRetryReRunsTheCancel()
    {
        var id = await StartLabAsync();

        var (status, body) = await TransitionAsync(id, "fr-cancel", new { failWith = "throw" });

        Assert.True(status == HttpStatusCode.OK, $"the failed cancel answered {(int)status}: {body}");
        Assert.Equal("F", StatusOf(body));
        await AssertFaultedInAsync(id, "fr-draft");
        await AssertActiveIncidentAsync(id, "fr-cancel", MappingFailed);

        var (retryStatus, retryBody) = await RetryAsync(id, new { });

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "fr-cancelled", "C");
        await AssertNoActiveIncidentAsync(id);
    }

    [Fact]
    public async Task CancelMappingThrows_CancellingAgainWithAValidBodyClosesTheInstance()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-cancel", new { failWith = "throw" });
        await AssertFaultedInAsync(id, "fr-draft");

        var (status, body) = await TransitionAsync(id, "fr-cancel", new { note = "second attempt" });

        Assert.True(status == HttpStatusCode.OK, $"cancel answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-cancelled", "C");
        await AssertNoActiveIncidentAsync(id);
        await AssertNoOpenRecordAsync(id);
    }

    [Fact]
    public async Task MappedDataFailsTheMasterSchema_FaultsWithAnOpenRecord_AndRetryReRunsTheHop()
    {
        var id = await StartLabAsync();

        var (status, body) = await TransitionAsync(id, "fr-advance", new { failWith = "schema" });

        Assert.True(status == HttpStatusCode.OK, $"the failed hop answered {(int)status}: {body}");
        await AssertFaultedInAsync(id, "fr-draft");
        await AssertActiveIncidentAsync(id, "fr-advance", errorCode: null);
        await AssertOpenRecordAsync(id, "fr-advance");

        var (retryStatus, retryBody) = await RetryAsync(id, new { note = "valid now" });

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "fr-active", "A");
        await AssertDataAsync(id, note: "valid now");
    }

    [Fact]
    public async Task AsyncManualMappingThrows_FaultsWithAnOpenRecord_AndRetryReRunsTheHop()
    {
        var id = await StartLabAsync();

        var (status, body) = await TransitionAsync(id, "fr-advance", new { failWith = "throw" }, sync: false);

        Assert.True((int)status < 400, $"the async hop was refused with {(int)status}: {body}");
        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Flow, id)).Status == "F",
            $"the async hop never faulted: {await DescribeAsync(Flow, id)}");
        await AssertActiveIncidentAsync(id, "fr-advance", MappingFailed);
        await AssertOpenRecordAsync(id, "fr-advance");

        var (retryStatus, retryBody) = await RetryAsync(id, new { note = "async fixed" });

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "fr-active", "A");
        await AssertDataAsync(id, note: "async fixed");
    }

    [Fact]
    public async Task RetryThatFailsAgain_StaysFaultedAndRetryable_OnTheSameRecord()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });
        await AssertFaultedInAsync(id, "fr-draft");

        var (againStatus, againBody) = await RetryAsync(id, new { failWith = "throw" });

        Assert.True(againStatus == HttpStatusCode.OK, $"the failing retry answered {(int)againStatus}: {againBody}");
        Assert.Equal("F", StatusOf(againBody));
        await AssertFaultedInAsync(id, "fr-draft");
        await AssertActiveIncidentAsync(id, "fr-advance", MappingFailed);
        Assert.Equal(1, await CountRecordsAsync(id, "fr-advance"));

        var (retryStatus, retryBody) = await RetryAsync(id, new { note = "third time" });

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "fr-active", "A");
        Assert.Equal(1, await CountRecordsAsync(id, "fr-advance"));
    }

    [Fact]
    public async Task UpdateDataWithAKeyAlreadyTaken_FaultsWithAnOpenRecord_AndRetryWithAFreeKeyAssignsIt()
    {
        // morph-touch's key-generate shape: it fires the keyless rezervation's `update-data` with data.key, and the
        // runtime assigns the key in the same step that runs the mapping — after checking no active instance holds
        // it. A taken key fails that step exactly like a throwing mapping.
        var holder = await StartLabAsync();
        var takenKey = await KeyOfAsync(holder);
        var id = await StartAsync(Flow, new { attributes = new { runId = Guid.NewGuid().ToString("N") } });

        var (status, body) = await TransitionAsync(id, "fr-update", new { note = "keyed" }, key: takenKey);

        Assert.True(status == HttpStatusCode.OK, $"the update answered {(int)status}: {body}");
        Assert.Equal("F", StatusOf(body));
        await AssertFaultedInAsync(id, "fr-draft");
        await AssertActiveIncidentAsync(id, "fr-update", DuplicateKey);
        await AssertOpenRecordAsync(id, "fr-update");

        var freeKey = $"fr-{Guid.NewGuid():N}";
        var (retryStatus, retryBody) = await RetryAsync(id, new { note = "keyed" }, key: freeKey);

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "fr-draft", "A");
        Assert.Equal(freeKey, await KeyOfAsync(id));
    }

    [Fact]
    public async Task CancelOnACompletedInstance_IsStillRefused()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { note = "ok" });
        await TransitionAsync(id, "fr-finish", new { });
        await AssertRestsAsync(id, "fr-done", "C");

        var (status, body) = await TransitionAsync(id, "fr-cancel", new { });

        Assert.True(status == HttpStatusCode.BadRequest, $"cancel on a completed instance answered {(int)status}: {body}");
        Assert.Contains("Instance:100018", body);
        await AssertRestsAsync(id, "fr-done", "C");
    }

    [Fact]
    public async Task CancellingAFaultedInstance_WhoseCancelMappingThrows_StaysFaultedAndRetryable()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });
        await AssertFaultedInAsync(id, "fr-draft");

        var (status, body) = await TransitionAsync(id, "fr-cancel", new { failWith = "throw" });

        Assert.True(status == HttpStatusCode.OK, $"the failed cancel answered {(int)status}: {body}");
        Assert.Equal("F", StatusOf(body));
        await AssertFaultedInAsync(id, "fr-draft");
        await AssertActiveIncidentAsync(id, "fr-cancel", MappingFailed);
        // The cancel took the failed advance over and closed its record; the cancel is the hop retry re-runs.
        await AssertOpenRecordAsync(id, "fr-cancel");

        var (retryStatus, retryBody) = await RetryAsync(id, new { });

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "fr-cancelled", "C");
        await AssertNoActiveIncidentAsync(id);
        await AssertNoOpenRecordAsync(id);
    }

    [Fact]
    public async Task AsyncCancel_OnAFaultedInstance_RunsInlineAndClosesIt()
    {
        // Queued as a job, the cancel waited on a still-Faulted instance, and the fault's own cleanup — arriving
        // a moment later through the Inbox — cancelled that job: 202, and the instance stayed Faulted (measured in
        // the full suite). A cancel on a Faulted instance therefore always runs inline.
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });
        await AssertFaultedInAsync(id, "fr-draft");

        var (status, body) = await TransitionAsync(id, "fr-cancel", new { }, sync: false);

        Assert.True(status == HttpStatusCode.OK, $"the async cancel answered {(int)status}: {body}");
        Assert.Equal("C", StatusOf(body));
        await AssertRestsAsync(id, "fr-cancelled", "C");
        await AssertNoActiveIncidentAsync(id);
        await AssertNoOpenRecordAsync(id);
    }

    [Fact]
    public async Task ChildMappingThrows_RetryingTheChildReRunsItsHop()
    {
        var (parentId, childId) = await StartParentWithChildAsync();

        var (status, body) = await TransitionAsync(childId, "fc-advance", new { failWith = "throw" }, flow: Child);

        Assert.True(status == HttpStatusCode.OK, $"the child's failed hop answered {(int)status}: {body}");
        await AssertFaultedInAsync(childId, "fc-draft", Child);
        await AssertOpenRecordAsync(childId, "fc-advance", Child);
        await WaitForStatusAsync(Parent, parentId, "F");

        var (retryStatus, retryBody) = await RetryAsync(childId, new { note = "child fixed" }, Child);

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(childId, "fc-done", "C", Child);
        await AssertNoActiveIncidentAsync(childId, Child);

        // Tripwire for the vnext known issue `parent-faulted-by-subflow-child-not-retryable`: the child's own fault
        // closed the parent's correlation, so the child's completion does not resume the parent. When that is fixed,
        // this assertion flips — update it together with the known issue's fixedIn.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal("F", (await GetInstanceStateAsync(Parent, parentId)).Status);
    }

    [Fact]
    public async Task ParentFaultedByItsChild_TheReservedCancelClosesEachOfThem()
    {
        var (parentId, childId) = await StartParentWithChildAsync();
        await TransitionAsync(childId, "fc-advance", new { failWith = "throw" }, flow: Child);
        await WaitForStatusAsync(Parent, parentId, "F");

        // Tripwire for the same known issue: the parent has neither an open correlation nor an open record of its
        // own, so its retry is refused — and must change nothing.
        var (retryStatus, retryBody) = await RetryAsync(parentId, new { }, Parent);
        Assert.True(retryStatus == HttpStatusCode.BadRequest && retryBody.Contains("Instance:100028"),
            $"the parent's retry answered {(int)retryStatus}: {retryBody}");
        Assert.Equal("F", (await GetInstanceStateAsync(Parent, parentId)).Status);

        var (status, body) = await TransitionAsync(parentId, "fp-cancel", new { }, flow: Parent);

        Assert.True(status == HttpStatusCode.OK, $"cancelling the parent answered {(int)status}: {body}");
        await AssertRestsAsync(parentId, "fp-cancelled", "C", Parent);
        await AssertNoActiveIncidentAsync(parentId, Parent);

        // The child's own fault closed the parent's correlation, so the parent's cancel does not reach it.
        var (childStatus, childBody) = await TransitionAsync(childId, "fc-cancel", new { }, flow: Child);

        Assert.True(childStatus == HttpStatusCode.OK, $"cancelling the child answered {(int)childStatus}: {childBody}");
        await AssertRestsAsync(childId, "fc-cancelled", "C", Child);
        await AssertNoActiveIncidentAsync(childId, Child);
    }

    [Fact]
    public async Task ParentUpdateDataThrowsWhileItsChildRuns_CancellingTheParentAlsoCancelsTheChild()
    {
        var (parentId, childId) = await StartParentWithChildAsync();

        var (status, body) = await TransitionAsync(parentId, "fp-update", new { failWith = "throw" }, flow: Parent);

        Assert.True(status == HttpStatusCode.OK, $"the failed update answered {(int)status}: {body}");
        await WaitForStatusAsync(Parent, parentId, "F");
        // The parent's fault cascades to the child it was waiting on, and keeps that correlation open.
        await WaitForStatusAsync(Child, childId, "F");

        // Tripwire for the vnext known issue `parent-faulted-by-subflow-child-not-retryable` (pre-existing, the refused
        // retry of vnext#1338): the parent's retry is handed to the cascaded child, which has nothing to re-run, and is
        // refused only after the parent was unfaulted — the parent is left Active with no incident. When that is fixed,
        // these assertions flip; update them together with the known issue's fixedIn.
        var (retryStatus, retryBody) = await RetryAsync(parentId, new { }, Parent);
        Assert.True(retryStatus == HttpStatusCode.BadRequest && retryBody.Contains("Instance:100028"),
            $"the parent's retry answered {(int)retryStatus}: {retryBody}");
        Assert.Equal("A", (await GetInstanceStateAsync(Parent, parentId)).Status);
        await AssertNoActiveIncidentAsync(parentId, Parent);

        var (cancelStatus, cancelBody) = await TransitionAsync(parentId, "fp-cancel", new { }, flow: Parent);

        Assert.True(cancelStatus == HttpStatusCode.OK, $"cancel answered {(int)cancelStatus}: {cancelBody}");
        await AssertRestsAsync(parentId, "fp-cancelled", "C", Parent);
        await AssertRestsAsync(childId, "fc-cancelled", "C", Child);
        await AssertNoActiveIncidentAsync(parentId, Parent);
        await AssertNoActiveIncidentAsync(childId, Child);
        // No open-record check here: the stranded retry left the parent Active, so this cancel took no Faulted
        // instance over and the failed fp-update record stays open — part of the same known issue.
    }

    [Fact]
    public async Task ParentFaultedByItsOutputMapping_TheReservedCancelClosesIt()
    {
        var (parentId, childId) = await StartParentWithChildAsync();

        var (status, body) = await TransitionAsync(childId, "fc-advance", new { note = "fail-output" }, flow: Child);

        Assert.True(status == HttpStatusCode.OK, $"the child's hop answered {(int)status}: {body}");
        await AssertRestsAsync(childId, "fc-done", "C", Child);
        await WaitForStatusAsync(Parent, parentId, "F");

        var (cancelStatus, cancelBody) = await TransitionAsync(parentId, "fp-cancel", new { }, flow: Parent);

        Assert.True(cancelStatus == HttpStatusCode.OK, $"cancel answered {(int)cancelStatus}: {cancelBody}");
        await AssertRestsAsync(parentId, "fp-cancelled", "C", Parent);
        await AssertNoActiveIncidentAsync(parentId, Parent);
    }

    // ── more transition kinds ────────────────────────────────────────────────

    [Fact]
    public async Task SharedTransitionMappingThrows_RetryReRunsIt()
    {
        var id = await StartLabAsync();

        await TransitionAsync(id, "fr-shared", new { failWith = "throw" });

        await AssertFaultedInAsync(id, "fr-draft");
        await AssertOpenRecordAsync(id, "fr-shared");
        var (status, body) = await RetryAsync(id, new { note = "shared" });
        Assert.True(status == HttpStatusCode.OK, $"retry answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-active", "A");
        await AssertDataAsync(id, note: "shared");
    }

    [Fact]
    public async Task HopIntoAFinishStateThrows_RetryCompletesTheInstance()
    {
        var id = await StartLabAsync();

        await TransitionAsync(id, "fr-finish-direct", new { failWith = "throw" });

        await AssertFaultedInAsync(id, "fr-draft");
        var (status, body) = await RetryAsync(id, new { note = "finished" });
        Assert.True(status == HttpStatusCode.OK, $"retry answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-done", "C");
        await AssertNoActiveIncidentAsync(id);
        await AssertNoOpenRecordAsync(id);
    }

    [Fact]
    public async Task HopIntoASubFlowStateThrows_RetryStartsTheChild_AndTheChildCompletesTheParent()
    {
        var id = await StartLabAsync();

        await TransitionAsync(id, "fr-spawn", new { failWith = "throw" });

        await AssertFaultedInAsync(id, "fr-draft");
        Assert.Empty(await GetActiveSubflowsAsync(Flow, id));
        var (status, body) = await RetryAsync(id, new { note = "spawned" });
        Assert.True(status == HttpStatusCode.OK, $"retry answered {(int)status}: {body}");
        string? childId = null;
        await WaitUntilAsync(async () =>
            {
                childId = (await GetActiveSubflowsAsync(Flow, id)).GetValueOrDefault(Child);
                return childId is not null && (await GetInstanceStateAsync(Child, childId)) == ("fc-draft", "A");
            },
            $"the retried hop started no child: {await DescribeAsync(Flow, id)}");

        await TransitionAsync(childId!, "fc-advance", new { note = "child done" }, flow: Child);

        await AssertRestsAsync(id, "fr-done", "C");
        Assert.Equal("child done", Text(await GetAttributesAsync(Flow, id), "childNote"));
    }

    [Fact]
    public async Task HopIntoATimedStateThrows_RetryArmsTheStatesTimer_AndItFires()
    {
        // The retry runs right after the fault, so the fault's own cleanup may arrive after the retry armed the
        // timer: it must not cancel it.
        var id = await StartLabAsync();

        await TransitionAsync(id, "fr-to-timed", new { failWith = "throw" });

        await AssertFaultedInAsync(id, "fr-draft");
        var (status, body) = await RetryAsync(id, new { note = "timed" });
        Assert.True(status == HttpStatusCode.OK, $"retry answered {(int)status}: {body}");
        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Flow, id)) == ("fr-active", "A"),
            $"the timer armed by the retried hop never fired: {await DescribeAsync(Flow, id)}", TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task MappingThatDoesNotCompile_FaultsEveryAttempt_AndTheReservedCancelClosesIt()
    {
        var id = await StartLabAsync();

        await TransitionAsync(id, "fr-broken", new { });

        await AssertFaultedInAsync(id, "fr-draft");
        await AssertOpenRecordAsync(id, "fr-broken");
        var (retryStatus, retryBody) = await RetryAsync(id, new { });
        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        Assert.Equal("F", StatusOf(retryBody));
        await AssertFaultedInAsync(id, "fr-draft");
        Assert.Equal(1, await CountRecordsAsync(id, "fr-broken"));

        var (status, body) = await TransitionAsync(id, "fr-cancel", new { });
        Assert.True(status == HttpStatusCode.OK, $"cancel answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-cancelled", "C");
        await AssertNoOpenRecordAsync(id);
    }

    // ── more modes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HopTheDefinitionMakesAsyncThrows_RetryReRunsIt()
    {
        var id = await StartLabAsync();

        // executionType "A" on the transition: the caller's sync=true does not matter.
        var (status, body) = await TransitionAsync(id, "fr-async", new { failWith = "throw" });

        Assert.True((int)status < 400, $"the hop was refused with {(int)status}: {body}");
        await WaitForStatusAsync(Flow, id, "F");
        await AssertOpenRecordAsync(id, "fr-async");
        var (retryStatus, retryBody) = await RetryAsync(id, new { note = "async again" });
        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "fr-active", "A");
        await AssertDataAsync(id, note: "async again");
    }

    [Fact]
    public async Task AsyncRetry_RightAfterTheFault_ReRunsTheHop()
    {
        // sync=false on retry queues the re-run; the fault's own cleanup may land after that job was queued and
        // must not cancel it.
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });

        var (status, body) = await RetryAsync(id, new { note = "queued retry" }, sync: false);

        Assert.True((int)status < 400, $"retry was refused with {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-active", "A");
        await AssertDataAsync(id, note: "queued retry");
    }

    // ── more retry behaviour ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateDataRetriedWithoutABody_RunsTheMappingOnNoBody()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-update", new { failWith = "throw", note = "lost" });

        var (status, body) = await RetryAsync(id, attributes: null);

        Assert.True(status == HttpStatusCode.OK, $"retry answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-draft", "A");
        var attributes = await GetAttributesAsync(Flow, id);
        Assert.Equal("fail-on-demand", Text(attributes, "mappedBy"));
        Assert.Equal("-", Text(attributes, "note"));
    }

    [Fact]
    public async Task SchemaFailureRetriedWithTheSameBody_FaultsAgainOnTheSameRecord_AndAValidBodyPasses()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "schema" });

        var (againStatus, againBody) = await RetryAsync(id, new { failWith = "schema" });

        Assert.True(againStatus == HttpStatusCode.OK, $"retry answered {(int)againStatus}: {againBody}");
        Assert.Equal("F", StatusOf(againBody));
        Assert.Equal(1, await CountRecordsAsync(id, "fr-advance"));
        var (status, body) = await RetryAsync(id, new { note = "valid" });
        Assert.True(status == HttpStatusCode.OK, $"retry answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-active", "A");
    }

    [Fact]
    public async Task AFailedHopWritesNoData_AndItsRetryWritesItOnce()
    {
        var id = await StartLabAsync();
        var (_, before, _) = await PollDataAsync(Flow, id);

        await TransitionAsync(id, "fr-advance", new { failWith = "throw", note = "never written" });

        var (_, afterFault, _) = await PollDataAsync(Flow, id);
        Assert.Equal(before, afterFault);
        Assert.Equal("-", Text(await GetAttributesAsync(Flow, id), "note"));
        await RetryAsync(id, new { note = "written once" });
        await AssertDataAsync(id, note: "written once");
        var (_, afterRetry, _) = await PollDataAsync(Flow, id);
        Assert.NotEqual(before, afterRetry);
    }

    [Fact]
    public async Task AfterRecovery_TheIncidentIsResolved_AndKeptInTheHistory()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });

        await RetryAsync(id, new { note = "recovered" });

        await AssertRestsAsync(id, "fr-active", "A");
        var incidents = await GetIncidentItemsAsync(id);
        var incident = Assert.Single(incidents);
        Assert.Equal(MappingFailed, Text(incident, "errorCode"));
        Assert.True(incident.GetProperty("isResolved").GetBoolean(), $"the incident is still open: {incident}");
    }

    [Fact]
    public async Task TheStateFunction_ShowsTheFaultAndItsIncident_ThenTheRecoveredState()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });

        var faulted = await StateBodyAsync(id);
        Assert.Equal("F", Text(faulted, "status"));
        Assert.True(faulted.GetProperty("incident").GetProperty("hasActiveIncident").GetBoolean());

        await RetryAsync(id, new { note = "recovered" });

        var recovered = await StateBodyAsync(id);
        Assert.Equal(("fr-active", "A"), (Text(recovered, "state"), Text(recovered, "status")));
        Assert.False(recovered.GetProperty("incident").GetProperty("hasActiveIncident").GetBoolean());
    }

    // ── what stays refused on a Faulted instance ─────────────────────────────

    [Fact]
    public async Task CancelNotAvailableInTheCurrentState_IsRefused_AndTheInstanceStaysFaultedAndRetryable()
    {
        var id = await StartAsync(Restricted, new { attributes = new { } });
        await TransitionAsync(id, "rr-advance", new { }, flow: Restricted);
        await TransitionAsync(id, "rr-fail", new { failWith = "throw" }, flow: Restricted);
        await AssertFaultedInAsync(id, "rr-active", Restricted);

        var (status, body) = await TransitionAsync(id, "rr-cancel", new { }, flow: Restricted);

        Assert.True((int)status >= 400 && body.Contains(NotAvailableInState),
            $"cancel outside its availableIn answered {(int)status}: {body}");
        await AssertFaultedInAsync(id, "rr-active", Restricted);
        await AssertActiveIncidentAsync(id, "rr-fail", MappingFailed, Restricted);
        var (retryStatus, retryBody) = await RetryAsync(id, new { }, Restricted);
        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "rr-done", "C", Restricted);
    }

    [Fact]
    public async Task FlowWithoutCancel_KeyTaken_RefusesCancel_AndRetryWithAFreeKeyIsTheWayOut()
    {
        // morph-touch's rezervation exactly: key-only updateData, no cancel declared.
        var holderKey = $"nc-{Guid.NewGuid():N}";
        await StartAsync(NoCancel, new { key = holderKey, attributes = new { } });
        var id = await StartAsync(NoCancel, new { attributes = new { } });

        var (updateStatus, updateBody) = await TransitionAsync(id, "nc-update", new { }, flow: NoCancel, key: holderKey);

        Assert.True(updateStatus == HttpStatusCode.OK, $"the update answered {(int)updateStatus}: {updateBody}");
        Assert.Equal("F", StatusOf(updateBody));
        await AssertFaultedInAsync(id, "nc-draft", NoCancel);
        await AssertActiveIncidentAsync(id, "nc-update", DuplicateKey, NoCancel);
        var (cancelStatus, cancelBody) = await TransitionAsync(id, "cancel", new { }, flow: NoCancel);
        Assert.True((int)cancelStatus >= 400, $"cancel on a flow without one answered {(int)cancelStatus}: {cancelBody}");
        await AssertFaultedInAsync(id, "nc-draft", NoCancel);

        var freeKey = $"nc-{Guid.NewGuid():N}";
        var (retryStatus, retryBody) = await RetryAsync(id, new { }, NoCancel, key: freeKey);

        Assert.True(retryStatus == HttpStatusCode.OK, $"retry answered {(int)retryStatus}: {retryBody}");
        await AssertRestsAsync(id, "nc-draft", "A", NoCancel);
        Assert.Equal(freeKey, await KeyOfAsync(id, NoCancel));
    }

    [Theory]
    [InlineData("fr-exit")]
    [InlineData("fr-advance")]
    [InlineData("fr-update")]
    public async Task AnyKeyButTheReservedCancel_IsStillRefusedOnAFaultedInstance(string transition)
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });

        var (status, body) = await TransitionAsync(id, transition, new { note = "too late" });

        Assert.True(status == HttpStatusCode.BadRequest && body.Contains(TerminalInstance),
            $"'{transition}' on a Faulted instance answered {(int)status}: {body}");
        await AssertFaultedInAsync(id, "fr-draft");
        await AssertActiveIncidentAsync(id, "fr-advance", MappingFailed);
    }

    // ── a fault after the record was saved (the case that already worked) ────

    [Fact]
    public async Task TaskFailureAfterTheRecordWasSaved_RetryStillReRunsTheHop()
    {
        var id = await StartLabAsync();

        await TransitionAsync(id, "fr-task", new { failTask = true });

        await AssertFaultedInAsync(id, "fr-draft");
        await AssertOpenRecordAsync(id, "fr-task");
        var (status, body) = await RetryAsync(id, new { failTask = false });
        Assert.True(status == HttpStatusCode.OK, $"retry answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-active", "A");
        Assert.Equal(1, await CountRecordsAsync(id, "fr-task"));
    }

    [Fact]
    public async Task TaskFailureAfterTheRecordWasSaved_TheReservedCancelClosesTheInstanceAndItsOpenRecord()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-task", new { failTask = true });
        await AssertFaultedInAsync(id, "fr-draft");

        var (status, body) = await TransitionAsync(id, "fr-cancel", new { });

        Assert.True(status == HttpStatusCode.OK, $"cancel answered {(int)status}: {body}");
        await AssertRestsAsync(id, "fr-cancelled", "C");
        await AssertNoActiveIncidentAsync(id);
        await AssertNoOpenRecordAsync(id);
    }

    // ── concurrent recovery requests ─────────────────────────────────────────

    [Fact]
    public async Task RetryAndCancelAtTheSameTime_EndInOneConsistentState()
    {
        for (var round = 0; round < 3; round++)
        {
            var id = await StartLabAsync();
            await TransitionAsync(id, "fr-advance", new { failWith = "throw" });
            await AssertFaultedInAsync(id, "fr-draft");

            var results = await Task.WhenAll(
                RetryAsync(id, new { note = "raced" }),
                TransitionAsync(id, "fr-cancel", new { }));

            Assert.All(results, r => Assert.True((int)r.Status < 500, $"a request failed with {(int)r.Status}: {r.Body}"));
            Assert.Contains(results, r => r.Status == HttpStatusCode.OK);
            await WaitUntilSettledAsync(Flow, id);
            var final = await GetInstanceStateAsync(Flow, id);
            Assert.True(final is ("fr-active", "A") or ("fr-cancelled", "C"),
                $"round {round}: the race left {final}: {await DescribeAsync(Flow, id)}");
            await AssertNoActiveIncidentAsync(id);
        }
    }

    [Fact]
    public async Task TwoRetriesAtTheSameTime_RunTheHopOnce()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });

        var results = await Task.WhenAll(RetryAsync(id, new { note = "first" }), RetryAsync(id, new { note = "second" }));

        Assert.All(results, r => Assert.True((int)r.Status < 500, $"a retry failed with {(int)r.Status}: {r.Body}"));
        Assert.Single(results, r => r.Status == HttpStatusCode.OK);
        await AssertRestsAsync(id, "fr-active", "A");
        Assert.Equal(1, await CountRecordsAsync(id, "fr-advance"));
        await AssertNoOpenRecordAsync(id);
    }

    [Fact]
    public async Task TwoCancelsAtTheSameTime_CloseTheInstanceOnce()
    {
        var id = await StartLabAsync();
        await TransitionAsync(id, "fr-advance", new { failWith = "throw" });

        var results = await Task.WhenAll(TransitionAsync(id, "fr-cancel", new { }), TransitionAsync(id, "fr-cancel", new { }));

        Assert.All(results, r => Assert.True((int)r.Status < 500, $"a cancel failed with {(int)r.Status}: {r.Body}"));
        Assert.Contains(results, r => r.Status == HttpStatusCode.OK);
        await AssertRestsAsync(id, "fr-cancelled", "C");
        await AssertNoActiveIncidentAsync(id);
    }

    // ── regression: a successful path is unchanged ───────────────────────────

    [Fact]
    public async Task ASuccessfulPath_LeavesOneFinishedRecordPerHop()
    {
        var id = await StartLabAsync();

        await TransitionAsync(id, "fr-advance", new { note = "ok" });
        await TransitionAsync(id, "fr-finish", new { });

        await AssertRestsAsync(id, "fr-done", "C");
        var records = await GetRecordsAsync(id);
        Assert.Equal(new[] { "start-faulted-recovery-lab", "fr-advance", "fr-finish" },
            records.Select(r => Text(r, "transitionId")).OrderBy(k => k == "start-faulted-recovery-lab" ? 0 : k == "fr-advance" ? 1 : 2).ToArray());
        Assert.DoesNotContain(records, IsOpen);
        Assert.Empty(await GetIncidentItemsAsync(id));
    }

    // ── requests ─────────────────────────────────────────────────────────────

    private async Task<string> StartLabAsync() =>
        await StartAsync(Flow, new { key = $"fr-{Guid.NewGuid():N}", attributes = new { runId = Guid.NewGuid().ToString("N") } });

    private async Task<(string ParentId, string ChildId)> StartParentWithChildAsync()
    {
        var parentId = await StartAsync(Parent, new { key = $"fp-{Guid.NewGuid():N}", attributes = new { } });
        string? childId = null;
        await WaitUntilAsync(async () =>
            {
                childId = (await GetActiveSubflowsAsync(Parent, parentId)).GetValueOrDefault(Child);
                return childId is not null && (await GetInstanceStateAsync(Child, childId)) == ("fc-draft", "A");
            },
            $"the child never rested in fc-draft: {await DescribeAsync(Parent, parentId)}");
        return (parentId, childId!);
    }

    private Task<(HttpStatusCode Status, string Body)> TransitionAsync(
        string id, string transition, object attributes, bool sync = true, string flow = Flow, string? key = null) =>
        SendRawAsync(HttpMethod.Patch,
            $"api/v1/{TestDomain.Name}/workflows/{flow}/instances/{id}/transitions/{transition}?sync={(sync ? "true" : "false")}",
            key is null ? new { attributes } : new { key, attributes }, Headers());

    private Task<(HttpStatusCode Status, string Body)> RetryAsync(
        string id, object? attributes, string flow = Flow, string? key = null, bool sync = true) =>
        SendRawAsync(HttpMethod.Post,
            $"api/v1/{TestDomain.Name}/workflows/{flow}/instances/{id}/retry?sync={(sync ? "true" : "false")}",
            attributes is null ? new { } : key is null ? new { attributes } : new { key, attributes }, Headers());

    private async Task<List<JsonElement>> GetIncidentItemsAsync(string id, string flow = Flow)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{flow}/instances/{id}/incidents?page=1&pageSize=20", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"incident history answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Select(i => i.Clone()).ToList();
    }

    private async Task<JsonElement> StateBodyAsync(string id)
    {
        var (status, _, body) = await PollStateAsync(Flow, id);
        Assert.True(status == HttpStatusCode.OK, $"the state function answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<string?> KeyOfAsync(string id, string flow = Flow)
    {
        var instance = await GetInstanceAsync(id, flow);
        return instance.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null;
    }

    // ── assertions ───────────────────────────────────────────────────────────

    private async Task AssertFaultedInAsync(string id, string state, string flow = Flow)
    {
        var instance = await GetInstanceAsync(id, flow);
        var metadata = instance.GetProperty("metadata");
        Assert.True(Text(metadata, "status") == "F" && Text(metadata, "currentState") == state,
            $"expected Faulted in {state}: {instance}");
        Assert.True(Text(metadata, "effectiveStatus") is "F" or "-",
            $"a faulted instance served effectiveStatus {Text(metadata, "effectiveStatus")}: {instance}");
    }

    private async Task AssertRestsAsync(string id, string state, string status, string flow = Flow)
    {
        await WaitUntilAsync(async () => (await GetInstanceStateAsync(flow, id)) == (state, status),
            $"expected {status} in {state}: {await DescribeAsync(flow, id)}");
    }

    private async Task WaitForStatusAsync(string flow, string id, string status) =>
        await WaitUntilAsync(async () => (await GetInstanceStateAsync(flow, id)).Status == status,
            $"expected status {status}: {await DescribeAsync(flow, id)}");

    private async Task AssertActiveIncidentAsync(string id, string transition, string? errorCode, string flow = Flow)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{flow}/instances/{id}/incidents/active", headers: Headers());

        Assert.True(status == HttpStatusCode.OK, $"no active incident ({(int)status}): {body}");
        var incident = JsonDocument.Parse(body).RootElement;
        Assert.Equal(transition, Text(incident, "transition"));
        if (errorCode is not null)
            Assert.Equal(errorCode, Text(incident, "errorCode"));
    }

    private async Task AssertNoActiveIncidentAsync(string id, string flow = Flow)
    {
        var instance = await GetInstanceAsync(id, flow);
        var metadata = instance.GetProperty("metadata");
        var active = metadata.TryGetProperty("incident", out var incident)
                     && incident.ValueKind == JsonValueKind.Object
                     && incident.TryGetProperty("active", out var flag)
                     && flag.ValueKind == JsonValueKind.True;
        Assert.False(active, $"the instance still carries an active incident: {instance}");
    }

    private async Task AssertOpenRecordAsync(string id, string transition, string flow = Flow)
    {
        var open = (await GetRecordsAsync(id, flow)).Where(IsOpen).ToList();
        Assert.True(open.Count == 1 && Text(open[0], "transitionId") == transition,
            $"expected exactly one open '{transition}' record, got: {string.Join(" | ", open.Select(r => r.ToString()))}");
    }

    private async Task AssertNoOpenRecordAsync(string id, string flow = Flow)
    {
        var open = (await GetRecordsAsync(id, flow)).Where(IsOpen).ToList();
        Assert.True(open.Count == 0, $"open records left behind: {string.Join(" | ", open.Select(r => r.ToString()))}");
    }

    private async Task<int> CountRecordsAsync(string id, string transition) =>
        (await GetRecordsAsync(id)).Count(r => Text(r, "transitionId") == transition);

    private async Task AssertDataAsync(string id, string note)
    {
        var attributes = await GetAttributesAsync(Flow, id);
        Assert.True(Text(attributes, "note") == note && Text(attributes, "mappedBy") == "fail-on-demand",
            $"expected note '{note}' written by the mapping: {attributes}");
    }

    // ── reads ────────────────────────────────────────────────────────────────

    private async Task<JsonElement> GetInstanceAsync(string id, string flow = Flow)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{flow}/instances/{id}", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"GET instance answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<List<JsonElement>> GetRecordsAsync(string id, string flow = Flow)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{flow}/instances/{id}/transitions", headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"transition history answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("transitions").EnumerateArray()
            .Select(r => r.Clone()).ToList();
    }

    private static bool IsOpen(JsonElement record) =>
        !record.TryGetProperty("finishedAt", out var finished) || finished.ValueKind == JsonValueKind.Null;

    private static string? StatusOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "-"
            : "-";
}
