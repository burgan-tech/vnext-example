using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.TaskInvocationLab;

/// <summary>
/// Proves the error boundary still selects the right handler from a task's RESULT when that task
/// ran on the in-process (Local) invocation path — the second of the three claims this scenario
/// exists for (vnext issue #1007). All three boundaries here are declared at task level on the
/// failing task's own <c>onExecutionTasks</c> entry (see <c>task-invocation-lab.json</c>), never
/// at state or workflow level, so <c>boundaryLevel</c> is always expected to be <c>Task</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The timeout case is wired through <c>errorTypes</c>, not <c>errorCodes</c> and not
/// <c>onTimeout</c> — both by necessity, confirmed empirically against the live incident rows,
/// not by reading source alone.</b>
/// <para>
/// <c>errorBoundary.onTimeout</c> is schema-valid, publishes fine, and is never read by
/// <c>CompiledBoundary.Compile</c> — a timeout handler authored the obvious way (<c>onTimeout</c>)
/// silently does nothing. An HTTP client timeout is not raised to the boundary as a distinguishable
/// "timeout" signal at all: it comes back as an ordinary <c>TaskInvocationResult.Failure</c> with
/// <c>StatusCode: null</c> and <c>Metadata["ExceptionType"] = "TaskCanceledException"</c> — a .NET
/// type name, not a timeout-shaped code (httpClient.Timeout expiring races, not cancels, the
/// caller's own <c>CancellationToken</c>, so <c>HttpTaskInvocation</c>'s cancellation-branch guard,
/// <c>when (cancellationToken.IsCancellationRequested)</c>, does not apply, and the exception falls
/// to the generic catch instead — the message text, "The request was canceled due to the configured
/// HttpClient.Timeout of 2 seconds elapsing", is the one positive proof that the in-process path
/// DOES honor the task's own <c>timeoutSeconds</c>, which is one of the things this lab exists to
/// verify).
/// </para>
/// <para>
/// <b>Two DIFFERENT matching mechanisms exist, keyed off two DIFFERENT fields of the same
/// <c>NormalizedError</c>, and this failure only satisfies one of them.</b> The business-failure
/// branch in <c>TaskExecutionEngine.ExecuteCoreAsync</c> normalizes the failure via
/// <c>ErrorNormalizer.NormalizeTaskResponse</c>, which sets <c>.Code</c> to
/// <c>Task:{TaskType}:{TaskKey}[:{StatusCode}]</c> (no exception type, ever) and separately sets
/// <c>.ExceptionType</c> from <c>Metadata["ExceptionType"]</c>; <c>.OriginalCode</c> is left null
/// (it is populated only from a <c>Metadata["ErrorCode"]</c> key that <c>HttpTaskInvocation</c>
/// never sets). With no Retry rule present, resolution always falls through to
/// <c>TaskExecutionEngine.HandlePostRetryFailureAsync</c>'s
/// <c>_boundaryResolver.ResolveExcluding(...)</c>, which resolves via
/// <c>CompiledBoundaryChain.FindMatchExcluding(NormalizedError error, ...)</c> — and THAT overload
/// matches on <c>error.OriginalCode</c> (always null here) and <c>error.ExceptionType</c>
/// ("TaskCanceledException"), <b>not</b> <c>error.Code</c>. A rule with only <c>errorCodes</c>
/// populated is therefore structurally unable to match this failure: <c>ErrorHandlerRule
/// .MatchesAnyCode</c> needs a non-null <c>errorCode</c> or a non-null <c>statusCode</c> to compare
/// against, and both are null for a client-side timeout — no string placed in <c>errorCodes</c>
/// (short form, full composite form, or the raw exception-type text) can ever satisfy it, and even
/// the wildcard-style catch-all confirms the boundary compiles and the pipeline mechanics are fine
/// once a matching rule exists. Only <c>errorTypes</c> reaches <c>ErrorHandlerRule
/// .MatchesExceptionType</c>, which compares against <c>error.ExceptionType</c> — the one field
/// that IS reliably "TaskCanceledException" for this failure. <c>til-http-slow</c>'s boundary is
/// therefore authored as <c>errorTypes: ["TaskCanceledException"]</c>, not <c>errorCodes</c>.
/// </para>
/// <para>
/// <b>Verified against the persisted incident row, not just the test's own assertions</b> (
/// <c>select "Task","ErrorCode","BoundaryAction" from task_invocation_lab."InstanceIncidents"
/// where "Task" = 'til-http-slow' order by "CreatedAt" desc limit 1</c>): with
/// <c>errorTypes: ["TaskCanceledException"]</c>, the newest row reads
/// <c>BoundaryAction = 'Rollback'</c> and the instance's <c>CurrentState = 'rolled-back'</c>,
/// <c>Status = 'C'</c>. With any <c>errorCodes</c>-only variant (including the exact composite
/// string <c>Task:Http:til-http-slow:TaskCanceledException</c> that a MATCHED rule never actually
/// needs to reproduce — see below), <c>BoundaryAction</c> stays NULL and the instance faults in
/// place at <c>ready</c>. Publishing changes to a live runtime while iterating on this rule
/// requires bumping the component's own <c>version</c> field each time — the SDK's/`wf update`'s
/// publish is a per-version no-op on a version it has already seen, which produced several
/// misleading "still fails" results while this file's <c>version</c> sat unchanged across edits.
/// </para>
/// <para>
/// <b>This is also why the incident's displayed <c>errorCode</c> differs depending on whether a
/// rule matched.</b> <c>BoundaryOutcomeHandler.BuildIncident</c> (the matched path) reads
/// <c>error.NormalizedError.Code</c> directly — the SHORT form, <c>Task:Http:til-http-slow</c>, no
/// exception type — which is what a matched incident's row actually shows. Only on the UNMATCHED
/// path does <c>TaskCoordinator.ProcessTaskResult</c> re-normalize the already-composed
/// <c>Error</c> from <c>ExecutionError.ToError()</c> (which DOES append <c>:{ExceptionType}</c>)
/// through <c>CreateFromError</c> — whose <c>BuildTaskCode</c> sees a code that already starts with
/// <c>"Task:"</c> and returns it verbatim — which is why an UNMATCHED incident's <c>errorCode</c>
/// looks like <c>Task:Http:til-http-slow:TaskCanceledException</c>, a string the boundary was never
/// actually offered to match against. This test therefore asserts the SHORT form.
/// </para>
/// <para>
/// <b>Authoring gap worth flagging (see the report for the issue writeup):</b> combined with
/// <c>onTimeout</c> being inert, there is currently no discoverable, generically-writable way for a
/// domain author to handle an HTTP task's own timeout. The only string that matches is the .NET
/// exception type name via <c>errorTypes</c> — undocumented, and indistinguishable at the schema
/// level from an <c>errorCodes</c> entry, so a plausible-looking <c>errorCodes:
/// ["TaskCanceledException"]</c> silently does nothing (confirmed above). <c>errorTypes: ["*"]</c>
/// is the only fully generic alternative, and it catches every exception-driven failure, not just a
/// timeout.
/// </para>
/// <para>
/// This class does not assert <c>retryCount</c> — none of these boundaries retry (Notify,
/// Rollback and Abort are all terminal-per-attempt actions) — so, unlike
/// <c>ErrorBoundaryLab.RetryPolicyTests</c>, there is nothing here to measure that field against.
/// </para>
/// </remarks>
public class ErrorBoundaryInteractionTests : TaskInvocationLabTestBase
{
    public ErrorBoundaryInteractionTests(VNextTestEnvironment environment) : base(environment) { }

    [SkippableFact]
    public async Task Http500_NotifyBoundary_LandsNotified_WithIncident()
    {
        Skip.If(!await IsMockLabUpAsync(),
            $"MockLab is not reachable at {MockLabBaseUrl()} — start it with `docker compose up -d` " +
            "in the repo root (or set MOCKLAB_BASE_URL).");

        var instanceId = await RunCaseAsync("case-http-500");

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("notified", state);
        Assert.NotEqual("F", status);

        var incident = BoundaryIncident(await GetIncidentItemsAsync(instanceId));
        Assert.Equal("Notify", Text(incident, "boundaryAction"));
        Assert.Equal("Task", Text(incident, "boundaryLevel"));
        Assert.Equal(500, Number(incident, "statusCode"));

        // FinalizeTransitionStep.ResolveIncidentOnSuccessfulErrorBoundaryTransition: an
        // error-boundary transition (this one — RequestNextTransition("to-notified",
        // TransitionRequestReasons.ErrorBoundary)) that completes WITHOUT a new fault resolves
        // every incident the failure left open, by design ("the boundary transition landed, the
        // failure is handled"). The Notify incident is therefore already resolved by the time the
        // instance lands on `notified` — confirmed by reading the raw incident (`isResolved: true,
        // resolvedAt: <ts>`) and `metadata.incident` (`hasActiveIncident: false`, no `active` key).
        var metadata = await GetIncidentMetadataAsync(instanceId);
        Assert.NotNull(metadata);
        Assert.False(Flag(metadata!.Value, "hasActiveIncident"),
            "a Notify boundary that routes to a transition resolves its own incident once that " +
            "transition lands without faulting");
        AssertAbsent(metadata.Value, "active", "resolved incidents carry no active-incident link");

        // The failing task's own OutputHandler still ran (same pattern as
        // ErrorBoundaryLab.ContinueActionsTests observing `httpAttempts` on a failed attempt): the
        // projection reflects the 500, not the transition's declared (and overridden) `landed` target.
        var projection = await GetProjectionAsync(instanceId);
        Assert.Equal("case-http-500", NullableString(projection, "tilCase"));
        Assert.Equal(500, NullableLong(projection, "tilStatusCode"));
    }

    [SkippableFact]
    public async Task HttpSlow_TimeoutViaOnError_RollbackBoundary_LandsRolledBack_WithIncident()
    {
        Skip.If(!await IsMockLabUpAsync(),
            $"MockLab is not reachable at {MockLabBaseUrl()} — start it with `docker compose up -d` " +
            "in the repo root (or set MOCKLAB_BASE_URL). This case also takes just over 2 seconds " +
            "to run — til-http-slow's timeoutSeconds:2 racing MockLab's delayMs:5000 route.");

        var instanceId = await RunCaseAsync("case-http-slow");

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("rolled-back", state);
        Assert.NotEqual("F", status);

        var incident = BoundaryIncident(await GetIncidentItemsAsync(instanceId));
        Assert.Equal("Rollback", Text(incident, "boundaryAction"));
        Assert.Equal("Task", Text(incident, "boundaryLevel"));
        // See this class's remarks: a MATCHED incident's errorCode is BuildIncident's short form
        // (NormalizedError.Code, Task:{TaskType}:{TaskKey} — no status here, and never an exception
        // type), not the longer string an UNMATCHED failure would display.
        Assert.Equal("Task:Http:til-http-slow", Text(incident, "errorCode"));

        // Same auto-resolve rule as Http500_NotifyBoundary above (FinalizeTransitionStep
        // .ResolveIncidentOnSuccessfulErrorBoundaryTransition): Rollback-with-a-transition lands
        // the same way Notify-with-a-transition does — the instance completes (Status 'C') and the
        // incident this boundary raised is already resolved by the time it gets here.
        var metadata = await GetIncidentMetadataAsync(instanceId);
        Assert.NotNull(metadata);
        Assert.False(Flag(metadata!.Value, "hasActiveIncident"),
            "a Rollback boundary that routes to a transition resolves its own incident once that " +
            "transition lands without faulting, exactly like Notify");
        AssertAbsent(metadata.Value, "active", "resolved incidents carry no active-incident link");

        // A client HttpClient timeout never produced an HTTP response: no status code, no body.
        var projection = await GetProjectionAsync(instanceId);
        Assert.Equal("case-http-slow", NullableString(projection, "tilCase"));
        Assert.Null(NullableLong(projection, "tilStatusCode"));
        Assert.Equal(0, NullableLong(projection, "tilBodyLength") ?? 0);
        Assert.False(BoolOrFalse(projection, "tilHasData"));
    }

    [SkippableFact]
    public async Task SoapFault_AbortBoundary_FaultsTheInstance_WithIncident()
    {
        Skip.If(!await IsMockLabUpAsync(),
            $"MockLab is not reachable at {MockLabBaseUrl()} — start it with `docker compose up -d` " +
            "in the repo root (or set MOCKLAB_BASE_URL).");

        var instanceId = await RunCaseAsync("case-soap-fault");
        await WaitUntilFaultedAsync(instanceId);

        // Abort has no `transition` in til-soap-fault's boundary — the instance faults from the
        // state it was already in (`ready`), it never reaches `landed`.
        var (state, _) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal(ReadyState, state);

        var items = await GetIncidentItemsAsync(instanceId);
        var incident = BoundaryIncident(items);
        Assert.Equal("Abort", Text(incident, "boundaryAction"));
        Assert.Equal("Task", Text(incident, "boundaryLevel"));
        Assert.Equal(500, Number(incident, "statusCode"));

        var metadata = await GetIncidentMetadataAsync(instanceId);
        Assert.NotNull(metadata);
        Assert.True(Flag(metadata!.Value, "hasActiveIncident"));
    }
}
