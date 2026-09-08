using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Runs as the SECOND task of a hook whose first task failed under a continue-style boundary
/// (<c>ignore</c> / <c>log</c>) or under no boundary at all.
/// <para>
/// Its only job is to stamp <c>markerAfterFailure</c>. Whether that key appears in instance data
/// answers a question no status or state can: after a continue-style outcome, does the runtime run
/// the REST of the hook, or does it stop at the failed task and continue the pipeline? The tests
/// assert the measured answer.
/// </para>
/// </summary>
public class EbMarkerMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["markerAfterFailure"] = true;
        target["markerAfterFailureAt"] = DateTime.UtcNow.ToString("o");

        LogInformation("EbMarkerMapping: marker stamped — the hook continued past the failed task");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
