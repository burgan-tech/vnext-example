using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Reads the three SubProcess start responses by their slots and records the started instance
/// ids. Distinct, non-empty ids prove each run kept its own slot: primaryChild / secondaryChild
/// (authored variableKey, same order) and pvkSpawnChild (legacy fallback, its own order).
/// </summary>
public class PvkRecordSlotsMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["primaryChildId"] = StartedId(context, "primaryChild");
        target["secondaryChildId"] = StartedId(context, "secondaryChild");
        target["legacyChildId"] = StartedId(context, "pvkSpawnChild");
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    // SubProcess start response nests the new instance under data.value.id; data.id is the
    // fallback shape (same reading as contract-signing's ContractStartOnlineMapping).
    private static object StartedId(ScriptContext context, string slot)
    {
        if (!context.TaskResponse.TryGetValue(slot, out var response) || response == null) return null;
        object id = null;
        try { id = response.data?.value?.id; } catch { id = null; }
        if (id == null) { try { id = response.data?.id; } catch { id = null; } }
        return id?.ToString();
    }
}
