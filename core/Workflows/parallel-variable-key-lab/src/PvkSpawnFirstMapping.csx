using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Starts one pvk-child SubProcess at order 1 of pvk-reuse and records its instance id as
/// firstChildId. Its start response stays in slot pvkSpawnChild, which the order-2 parallel group
/// then re-writes.
/// </summary>
public class PvkSpawnFirstMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask ?? throw new InvalidOperationException("Task must be a SubProcessTask");
        sub.SetKey(Guid.NewGuid().ToString());
        sub.SetBody(new { parentInstanceId = context.Instance.Id });
        sub.SetSync(false);
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        object id = null;
        try { id = context.Body?.data?.value?.id; } catch { id = null; }
        if (id == null) { try { id = context.Body?.data?.id; } catch { id = null; } }
        ((IDictionary<string, object>)result)["firstChildId"] = id?.ToString();
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
