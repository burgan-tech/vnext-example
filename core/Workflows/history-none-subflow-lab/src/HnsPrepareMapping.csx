using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// history-none-subflow-lab: writes parentToken before the SubFlow state. It is only in the parent's
/// in-memory buffer until the handoff writes it — the child's input mapping reads the parent from the
/// database, so a childEcho equal to parentToken proves the handoff flush.
/// </summary>
public class HnsPrepareMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var token = data != null && data.TryGetValue("token", out var value) ? value?.ToString() : "none";
        dynamic result = new ExpandoObject();
        ((IDictionary<string, object>)result)["parentToken"] = "tok-" + token;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
