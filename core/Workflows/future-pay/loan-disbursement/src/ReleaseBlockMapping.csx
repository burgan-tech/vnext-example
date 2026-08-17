using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T7 (execute-disbursement) order 1 — HTTP Task (type 6), MockLab.
/// Releases any cash block held against the customer before transferring funds.
/// MockLab answers with { "data": { ... } }, so the response body is unwrapped one extra level
/// (StandardTaskResponse.data → body.data) before the fields are read.
/// </summary>
public class ReleaseBlockMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask == null)
            throw new InvalidOperationException("Task must be an HttpTask");

        // Base url is configuration-driven: task definitions ship the API_BASEURL
        // placeholder so the same component runs against any environment.
        var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
        httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));

        var data = context.Instance?.Data;
        httpTask.SetBody(new
        {
            customerId = data?.application?.customerId,
            amount = data?.assessment?.approvedLimit,
            currency = data?.application?.currency
        });
        httpTask.SetHeaders(new Dictionary<string, string?>
        {
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json"
        });

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var statusCode = (int?)(context.Body?.statusCode) ?? 200;
        var payload = context.Body?.data ?? context.Body;
        dynamic inner = null;
        try { inner = payload?.data ?? payload; } catch { inner = payload; }

        var isSuccess = statusCode >= 200 && statusCode < 300;

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                blockReleased = isSuccess,
                releaseRef = inner?.releaseRef
            },
            Tags = new[] { "disbursement", "release-block", isSuccess ? "success" : "failure" }
        });
    }
}
