using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Task 12 (DirectTrigger) -> partner/xd-remote transition remote-advance, over Dapr.
/// Targets the instance by id when the start task recorded one, otherwise by the deterministic key.
/// </summary>
public class XdTriggerRemoteMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trigger = task as DirectTriggerTask ?? throw new InvalidOperationException("Task must be a DirectTriggerTask");
        var data = context.Instance?.Data as IDictionary<string, object>;

        trigger.SetDomain("partner");
        trigger.SetFlow("xd-remote");
        trigger.SetUseDapr(true);
        trigger.SetSync(true);
        trigger.SetTransitionName("remote-advance");

        if (data != null && data.TryGetValue("remoteInstanceId", out var id) && !string.IsNullOrWhiteSpace(id?.ToString()))
            trigger.SetInstance(id!.ToString());
        else if (data != null && data.TryGetValue("remoteKey", out var key))
            trigger.SetKey(key?.ToString());
        else
            throw new InvalidOperationException("xd-parent data carries neither remoteInstanceId nor remoteKey");

        trigger.SetBody(new { advancedBy = "xd-parent", advancedFrom = "core" });
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("XdTriggerRemoteMapping: remote-advance fired on partner/xd-remote");
        return Task.FromResult(new ScriptResponse { Data = new { remoteTriggered = true } });
    }
}
