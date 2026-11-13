using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

// `touch` ($self): changes the CHILD's data without changing the child's state. The parent's own
// data is untouched — exactly the moment the parent's data function keeps answering 304.
public class SdsTouchMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null)
        {
            foreach (var kv in inst)
            {
                target[kv.Key] = kv.Value;
            }
        }
        var current = 0;
        if (inst != null && inst.TryGetValue("childCounter", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }
        target["childCounter"] = current + 1;
        target["lastTouchAt"] = DateTime.UtcNow.ToString("o");
        LogInformation($"SdsTouchMapping: childCounter {current} -> {current + 1}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
