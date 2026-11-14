using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-xd icc-xd-sub-nest (partner), XC-06: a DirectTrigger (12) from the partner child BACK into
/// core (task config <c>domain: core</c>, <c>useDapr: true</c>), firing <c>touch</c> on the core target
/// <c>coreTargetId</c> the parent handed down. The mapping writes NO headers: the call carries only what the child's
/// own hop carried. If the parent withheld <c>sub</c>, the core target's <c>touchSeen</c> must not have one — not from
/// a header, not from trace baggage, not added by the Execution host.
/// OutputHandler records the call as <c>backTrigger</c>.
/// </summary>
public class IccXdBackTriggerMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trigger = task as DirectTriggerTask ?? throw new InvalidOperationException("Task must be a DirectTriggerTask");
        trigger.SetInstance(CoreTargetId(context));
        trigger.SetBody(new Dictionary<string, object> { ["touchedBy"] = context.Instance.Id.ToString() });
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        object body = context.Body;
        return Task.FromResult(new ScriptResponse
        {
            Data = new Dictionary<string, object>
            {
                ["backTrigger"] = new Dictionary<string, object>
                {
                    ["done"] = true,
                    ["isSuccess"] = Get(body, "isSuccess"),
                    ["statusCode"] = Get(body, "statusCode"),
                    ["error"] = Get(body, "errorMessage")?.ToString()
                }
            }
        });
    }

    private static string CoreTargetId(ScriptContext context) =>
        Get(context.Instance?.Data, "coreTargetId")?.ToString()
        ?? throw new InvalidOperationException("instance-call-credential-xd: the nested child has no 'coreTargetId'");

    private static object Get(object node, string name)
    {
        if (node is IDictionary<string, object> dict)
            foreach (var kv in dict)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
}
