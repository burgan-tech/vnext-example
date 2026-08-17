using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Input/Output mapping for the get-branches LOV function (cascade on $form.currency).
///
/// Parameter sources (in priority order):
///   1. `context.QueryString["currency"]` — for GET function calls (preferred for LOVs)
///   2. `context.Headers["currency"]`     — header fallback when the renderer carries
///                                          the filter as a header instead of a query
///   3. `context.Body?.currency`          — for POST-style function calls (back-compat)
/// Whichever is found gets appended to the underlying HTTP task URL.
///
/// Output — unwraps the StandardTaskResponse so the renderer's `x-lov` JsonPath
///          (`$.data[*].field`) resolves against the function's `data` field directly.
/// </summary>
public class GetBranchesLovMapping : ScriptBase, IMapping
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

            var currency = ResolveParam(context, "currency");

            if (!string.IsNullOrEmpty(currency))
            {
                var separator = httpTask.Url.Contains("?") ? "&" : "?";
                httpTask.SetUrl($"{httpTask.Url}{separator}currency={currency}");
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
                Key = "branches-lov-input-error",
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
                    Key = "branches-lov",
                    Data = new { data = items },
                    Tags = new[] { "lov", "branch", "cascade", "success" }
                });
            }

            return Task.FromResult(new ScriptResponse
            {
                Key = "branches-lov-failure",
                Data = new { error = "Failed to fetch branches", statusCode = statusCode },
                Tags = new[] { "lov", "branch", "failure" }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "branches-lov-exception",
                Data = new { error = ex.Message },
                Tags = new[] { "lov", "branch", "exception" }
            });
        }
    }

    private static string? ResolveParam(ScriptContext context, string name)
    {
        // 1. GET function call — query string (preferred for x-lookup)
        try
        {
            var dict = context.QueryParameters as Dictionary<string, object>;
            if (dict.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v?.ToString()))
                return v?.ToString();
        }
        catch { /* runtime may not expose QueryString — fall through */ }

        return null;
    }
}
