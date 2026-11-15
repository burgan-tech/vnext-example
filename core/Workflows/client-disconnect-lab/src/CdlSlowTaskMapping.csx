using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Mapping for the one slow HTTP task of the client-disconnect lab (vnext#1258).
/// <para>
/// <b>InputHandler</b> resolves the <c>API_BASEURL</c> placeholder from configuration, exactly like
/// <c>TilResultProjection</c> in task-invocation-lab, so the task reaches MockLab whether the runtime
/// runs on the host or in a container.
/// </para>
/// <para>
/// <b>OutputHandler</b> writes a small marker into instance data: which transition ran the task, the
/// HTTP status MockLab answered with and when the handler ran. The test reads it to prove the task
/// FINISHED after the client had already gone — the marker can only exist if the run was not
/// cancelled with the request.
/// </para>
/// </summary>
public class CdlSlowTaskMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");

        if (task is HttpTask httpTask)
        {
            httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;

        object? statusCode = null;
        if (body != null && body.TryGetValue("statusCode", out var raw))
        {
            statusCode = raw;
        }

        dynamic marker = new ExpandoObject();
        marker.transition = context.Transition?.Key ?? "unknown";
        marker.statusCode = statusCode;
        marker.finishedAtUtc = DateTime.UtcNow.ToString("o");

        dynamic result = new ExpandoObject();
        result.cdlSlowTask = marker;

        LogInformation($"CdlSlowTaskMapping: slow task finished on {context.Transition?.Key} status={statusCode}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
