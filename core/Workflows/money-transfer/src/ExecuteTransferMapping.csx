using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class ExecuteTransferMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var httpTask = task as HttpTask;
            if (httpTask == null)
                throw new InvalidOperationException("Task must be an HttpTask");

            httpTask.SetBody(new
            {
                sourceAccountId = (string)context.Instance?.Data?.sourceAccountId,
                targetIban = (string)context.Instance?.Data?.targetIban,
                amount = (decimal?)context.Instance?.Data?.amount,
                currency = (string)context.Instance?.Data?.currency
            });

            httpTask.SetHeaders(new Dictionary<string, string?>
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json",
                ["X-Request-Id"] = context.Headers?["x-request-id"] ?? Guid.NewGuid().ToString()
            });

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "execute-transfer-input-error",
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

            bool succeeded = statusCode >= 200 && statusCode < 300;
            string reference = null;
            try { reference = (string)payload?.transferReference; } catch { }

            return Task.FromResult(new ScriptResponse
            {
                Key = succeeded ? "execute-transfer-succeeded" : "execute-transfer-failed",
                Data = new { transferResult = new { success = succeeded, transferReference = reference, statusCode = statusCode } },
                Tags = new[] { "money-transfer", "execution", succeeded ? "success" : "failure" }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "execute-transfer-exception",
                Data = new { transferResult = new { success = false, error = ex.Message } },
                Tags = new[] { "money-transfer", "execution", "exception" }
            });
        }
    }
}
