using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

// customer-profile-enrichment — attaches customer/branch detail to instance reads
// for the application-intake state of the loan-disbursement workflow.
// type=3 DefinedFlows + scope=1 GetInstance: runs only on the single-instance GET endpoint
// of the flows it is referenced from (no Global/Everywhere performance cost).
public class CustomerProfileEnrichmentMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var httpTask = task as HttpTask;
            if (httpTask == null)
                throw new InvalidOperationException("Task must be an HttpTask");

            // Resolve customerId from the instance data so we enrich the right profile.
            string? customerId = null;
            try { customerId = context.Instance?.Data?.application?.customerId?.ToString(); } catch { }
            try { customerId ??= context.Body?.application?.customerId?.ToString(); } catch { }

            if (!string.IsNullOrEmpty(customerId))
            {
                var sep = httpTask.Url.Contains("?") ? "&" : "?";
                httpTask.SetUrl($"{httpTask.Url}{sep}customerId={customerId}");
            }

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
                Key = "customer-profile-enrichment-input-error",
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

            if (statusCode >= 200 && statusCode < 300 && payload != null)
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "customerProfile",
                    Data = new { customerProfile = payload },
                    Tags = new[] { "enrichment", "customer", "success" }
                });
            }

            return Task.FromResult(new ScriptResponse
            {
                Key = "customer-profile-enrichment-failure",
                Data = new { error = "Failed", statusCode = statusCode },
                Tags = new[] { "enrichment", "customer", "failure" }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "customer-profile-enrichment-exception",
                Data = new { error = ex.Message },
                Tags = new[] { "enrichment", "customer", "exception" }
            });
        }
    }
}
