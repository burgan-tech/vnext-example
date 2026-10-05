using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Starts one pvk-child SubProcess. Every run gets a fresh idempotency key, so the two order-1
/// runs create two distinct children whose start responses differ — the shape that made the
/// parallel merge throw before variableKey.
/// </summary>
public class PvkSpawnChildMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask ?? throw new InvalidOperationException("Task must be a SubProcessTask");
        sub.SetKey(Guid.NewGuid().ToString());
        sub.SetBody(new { parentInstanceId = context.Instance.Id });
        sub.SetSync(false);
        return Task.FromResult(new ScriptResponse());
    }

    // Delta-only: the slot recorder (order 3) reads the responses, nothing is written here.
    public Task<ScriptResponse> OutputHandler(ScriptContext context) =>
        Task.FromResult(new ScriptResponse { Data = new ExpandoObject() });
}
