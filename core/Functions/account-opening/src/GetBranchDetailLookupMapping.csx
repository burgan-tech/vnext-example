using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Input/Output mapping for the get-branch-detail lookup function.
///
/// IMPORTANT: this function is invoked **GET** by the renderer's `x-lookup`. GET function
/// calls do NOT carry a request body — parameters arrive via the query string or as
/// request headers. We resolve `code` from (1) QueryString, (2) Headers, (3) Body
/// (back-compat) and append it to the underlying HTTP task URL.
///
/// Output — unwraps the StandardTaskResponse so the renderer's `x-lookup.resultField`
///          (`$.data`) returns the lookup object directly. Result is exposed under
///          `$lookup.branchDetail.*` in view expressions.
/// </summary>
public class GetBranchDetailLookupMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var httpTask = task as HttpTask;
            if (httpTask == null)
                throw new InvalidOperationException("Task must be an HttpTask");

            var code = ResolveParam(context, "code");

            LogInformation("Code" + code);
            if (!string.IsNullOrEmpty(code))
            {
                var separator = httpTask.Url.Contains("?") ? "&" : "?";
                httpTask.SetUrl($"{httpTask.Url}{separator}code={code}");
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
                Key = "branch-detail-lookup-input-error",
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
            dynamic detail = null;
            try { detail = payload?.data ?? payload; } catch { detail = payload; }

            if (statusCode >= 200 && statusCode < 300 && detail != null)
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "branch-detail-lookup",
                    Data = new { data = detail },
                    Tags = new[] { "lookup", "branch", "success" }
                });
            }

            return Task.FromResult(new ScriptResponse
            {
                Key = "branch-detail-lookup-not-found",
                Data = new { error = "Branch not found", statusCode = statusCode },
                Tags = new[] { "lookup", "branch", "not-found" }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "branch-detail-lookup-exception",
                Data = new { error = ex.Message },
                Tags = new[] { "lookup", "branch", "exception" }
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
