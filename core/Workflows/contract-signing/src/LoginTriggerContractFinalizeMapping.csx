using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

// onExecution of login-finalize: DirectTrigger (type 12) the contract-flow's
// contract-finalize transition on the child instance captured at start.
public class LoginTriggerContractFinalizeMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trig = task as DirectTriggerTask;
        if (trig == null) throw new InvalidOperationException("Task must be a DirectTriggerTask");

        var data = context.Instance.Data as IDictionary<string, object>;
        object instanceId = null;
        if (data != null) data.TryGetValue("contractInstanceId", out instanceId);

        trig.SetDomain("core");
        trig.SetFlow("contract-flow");
        trig.SetTransitionName("contract-finalize");
        trig.SetInstance(instanceId?.ToString());
        trig.SetSync(false);
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("LoginTriggerContractFinalizeMapping: contract-finalize triggered on child");
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }
}
