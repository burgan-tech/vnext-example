using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onExecution of the finalize-loop $self transition (finalize-next): DirectTrigger
// online-finalize on onlineInstanceIds[finIndex], then advance finIndex. The $self auto loop
// re-fires until AllFinalizedRule routes to contract-completed (invocation-scoped job names
// make consecutive iterations safe).
public class ContractFinalizeOnlineMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trig = task as DirectTriggerTask;
        if (trig == null) throw new InvalidOperationException("Task must be a DirectTriggerTask");

        long fin = context.Instance.Data.finIndex;
        string onlineId = null;

        if (HasProperty(context.Instance.Data, "onlineInstanceIds"))
        {
            var ids = AsList(context.Instance.Data.onlineInstanceIds);
            // List<object> indexes with int, not long — `ids[fin]` throws
            // "best overloaded method match ... has some invalid arguments".
            int idx = (int)fin;
            if (idx >= 0 && idx < ids.Count) onlineId = ids[idx]?.ToString();
        }

        if (string.IsNullOrEmpty(onlineId))
            throw new InvalidOperationException($"ContractFinalizeOnlineMapping: no online instance id at finIndex {fin}");

        trig.SetDomain("core");
        trig.SetFlow("online-flow");
        trig.SetTransitionName("online-finalize");
        trig.SetInstance(onlineId);
        trig.SetSync(false);
        return Task.FromResult(new ScriptResponse { });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        long fin = context.Instance.Data.finIndex + 1;
        LogInformation($"ContractFinalizeOnlineMapping: finalized child {fin}");
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                finIndex = fin
            }
        });
    }
}