using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Reads the two slots left after the pvk-reuse parallel group and records the started instance
/// ids: pvkSpawnChild (re-written by the group, so it must differ from firstChildId) and
/// otherChild (authored variableKey, same group).
/// </summary>
public class PvkRecordReuseMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["reusedChildId"] = StartedId(context, "pvkSpawnChild");
        target["otherChildId"] = StartedId(context, "otherChild");
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    // Same reading as PvkRecordSlotsMapping: data.value.id, with data.id as the fallback shape.
    private static object StartedId(ScriptContext context, string slot)
    {
        if (!context.TaskResponse.TryGetValue(slot, out var response) || response == null) return null;
        object id = null;
        try { id = response.data?.value?.id; } catch { id = null; }
        if (id == null) { try { id = response.data?.id; } catch { id = null; } }
        return id?.ToString();
    }
}
