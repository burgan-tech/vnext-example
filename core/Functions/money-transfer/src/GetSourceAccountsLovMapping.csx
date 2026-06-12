using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class GetSourceAccountsLovMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var httpTask = task as HttpTask;
            if (httpTask == null)
                throw new InvalidOperationException("Task must be an HttpTask");

            httpTask.SetHeaders(new Dictionary<string, string?>
            {
                ["Accept"] = "application/json",
                ["X-Request-Id"] = context.Headers?["x-request-id"] ?? Guid.NewGuid().ToString()
            });

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "source-accounts-lov-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var statusCode = (int?)(context.Body?.statusCode) ?? 200;
            dynamic payload = context.Body?.data ?? context.Body;
            dynamic items = null;
            try { items = payload?.data ?? payload; } catch { items = payload; }

            if (statusCode >= 200 && statusCode < 300 && items != null)
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "source-accounts-lov",
                    Data = new { data = items },
                    Tags = new[] { "lov", "money-transfer", "success" }
                });
            }

            return Task.FromResult(new ScriptResponse
            {
                Key = "source-accounts-lov-failure",
                Data = new { error = "Failed", statusCode = statusCode },
                Tags = new[] { "lov", "money-transfer", "failure" }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "source-accounts-lov-exception",
                Data = new { error = ex.Message },
                Tags = new[] { "lov", "money-transfer", "exception" }
            });
        }
    }
}
