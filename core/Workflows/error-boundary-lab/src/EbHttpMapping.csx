using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Shared mapping for every HTTP failure injector in the lab (MockLab endpoints under
/// <c>api/eb-lab/*</c>).
/// <para>
/// The task components ship the <c>API_BASEURL</c> placeholder so the same component runs against
/// any environment; the base url is resolved here from configuration, exactly as the other example
/// flows do. A MockLab 500/503 makes the task fail with
/// <c>errorCode = Task:Http:{taskKey}:{status}</c> and that status on the incident.
/// </para>
/// <para>
/// One mapping is enough for all of them because the mapping carries no case-specific logic — the
/// case identity lives in the task component's url and the boundary attached to the task reference.
/// </para>
/// </summary>
public class EbHttpMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask != null)
        {
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
            httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // MEASURED: the output handler runs on EVERY attempt, failed ones included — `context.Body`
        // carries the failure response. So "this handler ran" proves nothing about success; the
        // status code does.
        var status = ReadStatusCode(context);
        var attempts = ReadInt(context, "httpAttempts") + 1;

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        // Attempt counter: the retry loop re-invokes the whole task, so this counts invocations.
        // A recovering retry therefore lands on 3 (1 + 2 retries) with the last status 200 — the
        // proof that a retry actually recovered, readable from instance data alone.
        target["httpAttempts"] = attempts;
        target["httpLastStatus"] = status;
        target["httpLastAt"] = DateTime.UtcNow.ToString("o");

        LogInformation($"EbHttpMapping: attempt {attempts} finished with status {status}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    private static int ReadStatusCode(ScriptContext context)
    {
        if (context.Body?.statusCode != null)
        {
            return (int)context.Body.statusCode;
        }

        return 0;
    }

    private static int ReadInt(ScriptContext context, string key)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        if (data == null || !data.TryGetValue(key, out var raw) || raw == null)
        {
            return 0;
        }

        return int.TryParse(raw.ToString(), out var parsed) ? parsed : 0;
    }
}
