using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// onExecute of the gate's scheduled transition. Its only job is to leave proof in instance data
/// that the timer really fired — the state alone would not distinguish "the timer fired" from
/// "something else moved the instance".
/// </summary>
public class TimeoutMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var current = 0;
        if (data != null && data.TryGetValue("timeoutFired", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["timeoutFired"] = current + 1;

        LogInformation($"TimeoutMapping: timeoutFired {current} -> {current + 1}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
