using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;
using BBT.Workflow.Tasks;

public class SetCacheMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var stateTask = (task as StateStoreTask);
        stateTask.SetCacheKey(context.Headers["x-device-id"]);
        stateTask.SetValue(new
        {
            context.Instance.Key
        });
        stateTask.SetCommand("set");

        return new ScriptResponse();
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // Process task response
        return new ScriptResponse();
    }
}
