using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T7 (execute-disbursement) order 1 — HTTP Task (type 6), MockLab.
/// Releases any cash block held against the customer before transferring funds.
/// </summary>
public class ReleaseBlockMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask == null)
            throw new InvalidOperationException("Task must be an HttpTask");

        var data = context.Instance?.Data;
        httpTask.SetBody(new
        {
            customerId = data?.application?.customerId,
            amount = data?.assessment?.approvedLimit,
            currency = data?.application?.currency
        });
        httpTask.SetHeaders(new Dictionary<string, string?>
        {
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json"
        });

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var payload = context.Body?.data ?? context.Body;
        return Task.FromResult(new ScriptResponse
        {
            Data = new { blockReleased = context.Body?.isSuccess ?? true, releaseRef = payload?.releaseRef },
            Tags = new[] { "disbursement", "release-block" }
        });
    }
}
