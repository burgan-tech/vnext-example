using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Shared mapping for every case in the task-invocation-lab scenario (issue #1007: Http, DaprService,
/// Soap, StateStore and CacheAside all run in-process on Orchestration now, behind a routing config
/// that can flip each type back to the Execution service).
/// <para>
/// <b>InputHandler</b> — the only per-invocation input work any case needs: resolve the
/// <c>API_BASEURL</c> placeholder on an <see cref="HttpTask"/> or <see cref="SoapTask"/>'s URL from
/// configuration, exactly like <c>EbHttpMapping</c> in error-boundary-lab. Every other task type in
/// this lab (DaprService, StateStore, CacheAside) has no URL to resolve, so the handler is a no-op
/// for them.
/// </para>
/// <para>
/// <b>OutputHandler</b> — writes ONE fixed-shape projection of the just-completed task's
/// <c>TaskInvocationResult</c> (already merged onto <c>context.Body</c> as a
/// <c>StandardTaskResponse</c> by the executor before this handler runs) into instance data:
/// <c>tilCase</c>, <c>tilTaskType</c>, <c>tilStatusCode</c>, <c>tilHasData</c>, <c>tilData</c>,
/// <c>tilBodyLength</c>, <c>tilMetadataKeys</c>, <c>tilMetadata</c>, <c>tilAt</c>. Deliberately
/// generic — no per-case branching — so the SAME projection lets a test read the state-store
/// round-trip value (<c>tilData</c>, written by <c>til-statestore-get</c>) and the cache-aside hit
/// flag (<c>tilMetadata.CacheHit</c>, written by <c>til-cacheaside</c>) without this file knowing
/// which case is running. <c>tilCase</c> comes from <c>context.Transition.Key</c> so a test can tell
/// the cases apart in instance data alone.
/// </para>
/// <para>
/// <c>tilTaskType</c> is intentionally NOT normalized (kept exactly as
/// <c>StandardTaskResponse.TaskType</c> stamps it). That field is the parity trap this whole scenario
/// exists to catch — see the build plan note that it was mis-stamped twice on this branch depending
/// on routing. Normalizing it here would hide a regression instead of surfacing it.
/// </para>
/// <para>
/// <b>CacheAside is the one exception to how this file gets attached.</b>
/// <c>CacheAsideTaskExecutor.ProcessOutputAsync</c> does not call the transition-level
/// <c>onExecutionTasks[].mapping</c>'s <c>OutputHandler</c> at all — it reads the task config's own
/// <c>sourceMapping</c> field instead. <c>til-cacheaside.json</c> therefore points BOTH its
/// transition-level <c>mapping</c> (harmless no-op InputHandler here) AND its task-level
/// <c>config.sourceMapping</c> at this same file, so the projection still runs for that case.
/// </para>
/// </summary>
public class TilResultProjection : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");

        if (task is HttpTask httpTask)
        {
            httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));
        }
        else if (task is SoapTask soapTask)
        {
            soapTask.SetUrl(soapTask.Url.Replace("API_BASEURL", apiBaseUrl));
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;

        var caseKey = context.Transition?.Key ?? "unknown";
        var taskType = ReadString(body, "taskType");
        var statusCode = ReadNullableLong(body, "statusCode");
        var data = ReadValue(body, "data");
        var rawBody = ReadString(body, "body");
        var metadata = ReadValue(body, "metadata") as IDictionary<string, object>;

        var metadataKeys = new List<object>();
        if (metadata != null)
        {
            foreach (var key in metadata.Keys)
            {
                metadataKeys.Add(key);
            }
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        // Delta-only: the write service merges this into the DB head under the per-instance lock.
        target["tilCase"] = caseKey;
        target["tilTaskType"] = taskType ?? string.Empty;
        target["tilStatusCode"] = statusCode.HasValue ? (object)statusCode.Value : null;
        target["tilHasData"] = data != null;
        target["tilData"] = data;
        target["tilBodyLength"] = rawBody?.Length ?? 0;
        target["tilMetadataKeys"] = metadataKeys;
        target["tilMetadata"] = metadata;
        target["tilAt"] = DateTime.UtcNow.ToString("o");

        LogInformation(
            $"TilResultProjection: case={caseKey} taskType={taskType} statusCode={statusCode} hasData={data != null}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    private static string? ReadString(IDictionary<string, object>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var raw) || raw == null)
        {
            return null;
        }

        return raw.ToString();
    }

    private static long? ReadNullableLong(IDictionary<string, object>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var raw) || raw == null)
        {
            return null;
        }

        return raw switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => long.TryParse(raw.ToString(), out var parsed) ? parsed : (long?)null
        };
    }

    private static object? ReadValue(IDictionary<string, object>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var raw))
        {
            return null;
        }

        return raw;
    }
}
