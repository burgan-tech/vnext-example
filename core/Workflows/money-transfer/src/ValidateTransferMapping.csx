using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

public class ValidateTransferMapping : ScriptBase, IMapping
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
                Key = "validate-transfer-input-error",
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

            bool valid = false;
            try { valid = (bool?)(payload?.valid) ?? (statusCode >= 200 && statusCode < 300); }
            catch { valid = statusCode >= 200 && statusCode < 300; }

            string reason = null;
            try { reason = (string)payload?.reason; } catch { }

            return Task.FromResult(new ScriptResponse
            {
                Key = valid ? "validate-transfer-passed" : "validate-transfer-failed",
                Data = new { validationPassed = valid, validationReason = reason, statusCode = statusCode },
                Tags = new[] { "money-transfer", "validation", valid ? "success" : "failure" }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "validate-transfer-exception",
                Data = new { validationPassed = false, error = ex.Message },
                Tags = new[] { "money-transfer", "validation", "exception" }
            });
        }
    }
}
