using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Task 1 mapping for multi-task function test.
/// Calls validate-account-policies HTTP task with instance data.
/// </summary>
public class FunctionValidatePoliciesMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask == null)
            return Task.FromResult(new ScriptResponse());

        var userSession = context.Instance?.Data?.userSession;
        var accountType = context.Instance?.Data?.accountType;

        httpTask.SetBody(new
        {
            userId = userSession?.userId,
            accountType = accountType ?? "demand-deposit",
            currency = context.Instance?.Data?.currency,
            requestedAt = DateTime.UtcNow
        });

        httpTask.SetHeaders(new Dictionary<string, string?>
        {
            ["Content-Type"] = "application/json",
            ["X-Instance-Id"] = context.Instance?.Id.ToString(),
            ["X-Request-Id"] = context.Headers?["x-request-id"] ?? Guid.NewGuid().ToString()
        });

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse
        {
            Key = "policy-check-result",
            Data = context.Body
        });
    }
}
