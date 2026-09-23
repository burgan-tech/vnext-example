using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// til-cached-echo — the probe for the FUNCTION RESPONSE CACHE path
/// (<c>IStateStoreCacheGateway</c> → <c>ITaskInvocationDispatcher</c>), which issue #1007 moved onto
/// the same routing seam as the five wire task types. No other scenario in this repository configures
/// <c>function.cache</c>, so without this function that gateway is never exercised end to end and the
/// <c>Cache.Get</c>/<c>Cache.Set</c> spans it emits (component type <c>function-response</c>) never
/// appear in a trace.
/// <para>
/// Deliberately trivial: it wraps the <c>til-http-ok</c> MockLab call so a cache MISS costs one
/// outbound HTTP round trip and a HIT costs none. The measurable difference between the two is the
/// whole point — the function's own body must not add noise.
/// </para>
/// </summary>
public class TilCachedEchoMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // The task definition ships the API_BASEURL placeholder and relies on its INPUT HANDLER to
        // resolve it. A function supplies its own mapping, which replaces the task's — so without
        // this line the URL stays relative and the call dies with "request URI must be absolute".
        if (task is HttpTask httpTask)
        {
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
            httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic payload = new ExpandoObject();
        var target = (IDictionary<string, object>)payload;

        // A value that changes per execution: when the cache serves the response, this stays
        // frozen at the value computed on the miss — which is how a test tells a hit from a miss
        // without reading spans.
        target["computedAtUtc"] = DateTime.UtcNow.ToString("O");
        target["source"] = "til-cached-echo";

        dynamic response = new ExpandoObject();
        ((IDictionary<string, object>)response)["data"] = payload;

        LogInformation("TilCachedEchoMapping: response computed (this line does NOT run on a cache hit)");
        return Task.FromResult(new ScriptResponse { Data = response });
    }
}
