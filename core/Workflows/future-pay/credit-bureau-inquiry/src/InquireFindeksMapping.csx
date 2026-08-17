using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// onEntry HTTP mapping for inquire-findeks (Task type 6) in the credit-bureau-inquiry subflow.
/// Sends customerId to MockLab; writes the Findeks note onto instance data.
/// </summary>
public class InquireFindeksMapping : ScriptBase, IMapping
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
        var customerId = data?.customerId?.ToString() ?? data?.application?.customerId?.ToString();

        httpTask.SetBody(new { customerId = customerId ?? string.Empty });
        httpTask.SetHeaders(new Dictionary<string, string?>
        {
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json"
        });

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // MockLab answers with { "data": { ... } }, so unwrap one level past StandardTaskResponse.data.
        var payload = context.Body?.data ?? context.Body;
        dynamic inner = null;
        try { inner = payload?.data ?? payload; } catch { inner = payload; }

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                findeksNote = inner?.findeksNote,
                inquiryStatus = "findeks-completed"
            },
            Tags = new[] { "credit-bureau", "findeks" }
        });
    }
}
