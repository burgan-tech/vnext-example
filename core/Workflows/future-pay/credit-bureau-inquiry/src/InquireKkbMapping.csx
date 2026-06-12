using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// onEntry HTTP mapping for inquire-kkb (Task type 6) in the credit-bureau-inquiry subflow.
/// Sends customerId to MockLab; writes the KKB score / total debt onto instance data.
/// </summary>
public class InquireKkbMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask == null)
            throw new InvalidOperationException("Task must be an HttpTask");

        var data = context.Instance?.Data;
        var customerId = data?.customerId?.ToString() ?? data?.application?.customerId?.ToString();

        httpTask.SetBody(new { customerId = customerId ?? string.Empty });
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
            Data = new
            {
                kkbScore = payload?.kkbScore,
                totalExistingDebt = payload?.totalExistingDebt,
                inquiryDate = DateTime.UtcNow.ToString("o"),
                inquiryStatus = "kkb-completed"
            },
            Tags = new[] { "credit-bureau", "kkb" }
        });
    }
}
