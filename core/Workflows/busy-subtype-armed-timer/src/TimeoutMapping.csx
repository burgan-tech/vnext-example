using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// onExecute of the wait state's scheduled transition: leaves proof in instance data that the
/// timer really fired.
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
