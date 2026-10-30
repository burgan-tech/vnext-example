using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// subflow-carried-headers-lab: onEntry of the two route states the parent's resume can reach
/// (sfch-resume-saw-sub / sfch-resume-no-sub). Records which one was entered as <c>resumeRoute</c>,
/// taken from the current state key, so one mapping serves both states.
/// </summary>
public class SfchRouteMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var state = context.Instance.CurrentState ?? "";
        dynamic result = new ExpandoObject();
        result.resumeRoute = state.EndsWith("saw-sub") ? "saw-sub" : state.EndsWith("no-sub") ? "no-sub" : "unknown:" + state;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
