using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start task for fan-out-documents. Runs once, before the batch, and does two things:
/// counts the caller-supplied <c>documents</c> array so the test has an independent expected
/// total, and stamps the instance-data version as it stands BEFORE anything fan-out related
/// has written.
/// <para>
/// The version stamp is the scenario's instrument for the single-write invariant. There is no
/// orchestration-host endpoint that enumerates instance-data versions (only the monitoring host
/// has <c>versionHistory</c>), so the flow reports its own version marks into instance data and
/// the test does the arithmetic. <c>context.Instance.LatestData.Version</c> is read BEFORE this
/// task's own output is applied, so it names the row this task is about to supersede.
/// </para>
/// </summary>
public class FanOutStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;

        var data = context.Instance?.Data as IDictionary<string, object>;

        var documentCount = 0;
        // FULLY QUALIFIED deliberately. The script host imports System.Collections.Generic, so a
        // bare `IEnumerable` binds to the GENERIC IEnumerable<T> and the script fails to compile
        // with CS0305 — which faults the instance at start, before the fan-out is ever reached.
        if (data != null && data.TryGetValue("documents", out var raw) &&
            raw is System.Collections.IEnumerable list && !(raw is string))
        {
            foreach (var _ in list) documentCount++;
        }

        // Delta-only by design: return ONLY the keys this task owns.
        target["fanOutStarted"] = true;
        target["documentCount"] = documentCount;
        target["versionBeforeFanOut"] = context.Instance?.LatestData?.Version ?? string.Empty;
        target["startedAtUtc"] = DateTime.UtcNow.ToString("o");

        LogInformation($"FanOutStartMapping: documentCount={documentCount} versionBeforeFanOut={target["versionBeforeFanOut"]}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
