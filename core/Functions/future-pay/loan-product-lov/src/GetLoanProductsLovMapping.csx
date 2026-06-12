using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

// loan-product-lov — REST LOV feeding the product dropdown in the application-intake form.
// Renderer invokes via GET (x-lov). Output is wrapped as { data: [...] } so the renderer's
// JsonPath $.data[*].code / $.data[*].label resolve cleanly.
public class GetLoanProductsLovMapping : IMapping
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
                Key = "loan-product-lov-input-error",
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
                    Key = "loan-product-lov",
                    Data = new { data = items },
                    Tags = new[] { "lov", "loan", "success" }
                });
            }

            return Task.FromResult(new ScriptResponse
            {
                Key = "loan-product-lov-failure",
                Data = new { error = "Failed", statusCode = statusCode },
                Tags = new[] { "lov", "loan", "failure" }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "loan-product-lov-exception",
                Data = new { error = ex.Message },
                Tags = new[] { "lov", "loan", "exception" }
            });
        }
    }
}
