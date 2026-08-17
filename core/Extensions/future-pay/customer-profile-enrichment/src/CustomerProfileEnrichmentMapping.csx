using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

// customer-profile-enrichment — attaches customer/branch detail to instance reads
// for the application-intake state of the loan-disbursement workflow.
// type=3 DefinedFlows + scope=1 GetInstance: runs only on the single-instance GET endpoint
// of the flows it is referenced from (no Global/Everywhere performance cost).
public class CustomerProfileEnrichmentMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var httpTask = task as HttpTask;
            if (httpTask == null)
                throw new InvalidOperationException("Task must be an HttpTask");

            // Base url is configuration-driven: task definitions ship the API_BASEURL
            // placeholder so the same component runs against any environment.
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
            httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));

            // Resolve customerId from the instance data so we enrich the right profile.
            // The submit-application payload lands at the root of instance data; the `application`
            // section only exists once validate-application has projected it.
            string? customerId = null;
            try { customerId = context.Instance?.Data?.application?.customerId?.ToString(); } catch { }
            try { customerId ??= context.Instance?.Data?.customerId?.ToString(); } catch { }
            try { customerId ??= context.Body?.application?.customerId?.ToString(); } catch { }
            try { customerId ??= context.Body?.customerId?.ToString(); } catch { }

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
            // MockLab answers with { "data": { ... } }; without this second unwrap the profile
            // lands as customerProfile.data.fullName instead of customerProfile.fullName.
            dynamic payload = context.Body?.data ?? context.Body;
            dynamic profile = null;
            try { profile = payload?.data ?? payload; } catch { profile = payload; }

            if (statusCode >= 200 && statusCode < 300 && profile != null)
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "customerProfile",
                    Data = new { customerProfile = profile },
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
