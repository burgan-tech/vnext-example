using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// The SOURCE task's mapping for every CacheAside (type 18) case in task-invocation-lab — the
/// task config's <c>sourceMapping</c>, not a transition-level mapping (vnext issue #1048: the source
/// runs as a task, with this file as its <c>IMapping</c>).
/// <para>
/// <b>InputHandler</b> runs on a cache MISS only, before the source call, against the resolved source
/// task. It configures what the source's static config deliberately leaves open:
/// an <see cref="HttpTask"/> (<c>til-cache-source</c>) gets its <c>API_BASEURL</c> placeholder resolved
/// from configuration, exactly like <c>TilResultProjection</c> does for the other til HTTP tasks; a
/// <see cref="GetInstanceDataTask"/> (<c>til-getdata-source</c>, which has no static key) gets its
/// target instance key from this instance's own data (<c>targetKey</c>, written by the test at start).
/// That second branch is the #1048 acceptance case: before the fix the InputHandler never ran and the
/// source called <c>/instances//data</c>.
/// </para>
/// <para>
/// <b>OutputHandler</b> runs after the source call; its <c>Data</c> is what gets CACHED. It wraps the
/// source's parsed response as <c>{ shaped = true, computedAtUtc, payload }</c>. <c>shaped</c> proves
/// the cached value is this handler's output rather than the raw source payload; <c>computedAtUtc</c>
/// is stamped once, at source time, so a later cache HIT returns the SAME stamp — a test reading it
/// twice can tell a hit from a second source call without trusting the <c>CacheHit</c> flag alone.
/// </para>
/// </summary>
public class TilCacheSourceMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        if (task is HttpTask httpTask)
        {
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
            httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));
        }
        else if (task is GetInstanceDataTask getDataTask)
        {
            var data = context.Instance?.Data as IDictionary<string, object>;
            var targetKey = data != null && data.TryGetValue("targetKey", out var raw) ? raw?.ToString() : null;
            getDataTask.SetKey(targetKey);
            LogInformation($"TilCacheSourceMapping: GetInstanceData source key set to '{targetKey}'");
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        object? payload = null;
        if (body != null && body.TryGetValue("data", out var data))
        {
            payload = data;
        }

        dynamic shaped = new ExpandoObject();
        shaped.shaped = true;
        shaped.computedAtUtc = DateTime.UtcNow.ToString("o");
        shaped.payload = payload;

        return Task.FromResult(new ScriptResponse { Data = shaped });
    }
}
