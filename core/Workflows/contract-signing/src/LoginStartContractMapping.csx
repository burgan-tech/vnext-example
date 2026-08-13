using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onEntry of login-initial: start the contract-flow SubProcess (type 14),
// passing this Login instance id as contractId + the parent callback transition refs.
// OutputHandler captures the started contract instance id (response.id) into instance data.
public class LoginStartContractMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask;
        if (sub == null) throw new InvalidOperationException("Task must be a SubProcessTask");

        sub.SetDomain("core");
        sub.SetFlow("contract-flow");
        sub.SetVersion("1.1.0");

        sub.SetBody(new
        {
            sub = context.Instance.Data.sub,
            act_sub = context.Instance.Data.act_sub,
            contractCode = context.Instance.Data.contractCode,
            contractId = context.Instance.Id,              // this Login instance id = idempotency key + callback target
            readyTransition = new { domain = "core", flow = "login-flow", key = "login-ready" },
            approvalsDoneTransition = new { domain = "core", flow = "login-flow", key = "login-approvals-done" },
            completedTransition = new { domain = "core", flow = "login-flow", key = "contract-status" }
        });

        sub.SetKey(context.Instance.Id.ToString());
        sub.SetSync(false);
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("LoginStartContractMapping: contract subprocess started, contractInstanceId captured");
        return Task.FromResult(new ScriptResponse { Data = new
        {
            contractInstanceId = context.Body.data.value.id
        } });
    }
}
