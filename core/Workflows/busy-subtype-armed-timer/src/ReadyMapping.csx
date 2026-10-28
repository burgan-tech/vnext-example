using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// onExecute of the auto gate: counts the hop, so the test can tell "the gate opened once" from
/// "something else moved the instance".
/// </summary>
public class ReadyMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var current = 0;
        if (data != null && data.TryGetValue("readyAdvances", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["readyAdvances"] = current + 1;

        LogInformation($"ReadyMapping: readyAdvances {current} -> {current + 1}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
