using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;
using BBT.Workflow.Tasks;

public class GetCacheMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var stateTask = (task as StateStoreTask);
        stateTask.SetCacheKey(context.Headers["x-device-id"]);
        stateTask.SetValue(new
        {
            context.Instance.Key
        });
        stateTask.SetCommand("get");


        return new ScriptResponse();
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic payload = context.Body?.data ?? context.Body;
        dynamic detail = null;
        try { detail = payload?.data ?? payload; } catch { detail = payload; }
        // Process task response
        return Task.FromResult(new ScriptResponse
        {
            Key = "get-cache-instance",
            Data = new { data = detail },
            Tags = new[] { "lookup", "branch", "success" }
        });
    }
}
