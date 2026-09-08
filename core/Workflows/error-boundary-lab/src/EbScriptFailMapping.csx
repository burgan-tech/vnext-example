using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Deliberate, caller-controlled task failure — the lab's failure injector that needs no MockLab.
/// <para>
/// Throws from <c>OutputHandler</c> unless instance data carries <c>shouldFail: false</c>. The
/// runtime wraps the exception as an infrastructure-level task error, so the incident it produces
/// reads <c>errorCode = Task:Script:{taskKey}:500</c>, <c>statusCode = 500</c>,
/// <c>errorLayer = "Task"</c>.
/// </para>
/// <para>
/// The flag is read from INSTANCE DATA, not from <c>context.Body</c>: in an OutputHandler
/// <c>Body</c> is the task's own response, while a transition payload (including the body of
/// <c>POST .../instances/{id}/retry</c>) is merged into instance data by
/// <c>CreateTransitionRecordStep</c> (order 20) before OnExecute (30) runs. That is what lets a
/// retry flip the outcome: retrying with <c>{"attributes":{"shouldFail":false}}</c> makes the very
/// same task succeed.
/// </para>
/// </summary>
public class EbScriptFailMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        if (ShouldFail(context))
        {
            LogInformation("EbScriptFailMapping: failing deliberately (shouldFail is not false)");
            throw new InvalidOperationException(
                "error-boundary-lab: deliberate script failure (set shouldFail=false to recover)");
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        // Delta-only: the write service merges this into the DB head under the per-instance lock.
        target["scriptRecovered"] = true;
        target["scriptRecoveredAt"] = DateTime.UtcNow.ToString("o");

        LogInformation("EbScriptFailMapping: recovered (shouldFail=false)");
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    private static bool ShouldFail(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        if (data == null || !data.TryGetValue("shouldFail", out var raw) || raw == null)
        {
            // Absent flag means "fail" so a case that never sets it still exercises the boundary.
            return true;
        }

        return !bool.TryParse(raw.ToString(), out var parsed) || parsed;
    }
}
