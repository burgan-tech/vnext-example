using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Records what a mapping can see of the request headers (vnext#1260, Workflow:HeaderExposure:Deny): the sorted header
/// names as one string and whether the probe header was visible. Runs on the start transition and on echo-headers, each
/// into its own fields, so one instance shows both hops.
/// </summary>
public class EchoHeadersMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var names = new List<string>();
        if (context.Headers is IDictionary<string, string> headers)
        {
            names.AddRange(headers.Keys.Select(k => k.ToLowerInvariant()));
        }

        names.Sort(StringComparer.Ordinal);
        var prefix = context.Transition?.Key == "echo-headers" ? "echo" : "start";

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target[prefix + "HeaderNames"] = string.Join(",", names);
        target[prefix + "SawProbe"] = names.Contains("x-exposure-probe");
        target[prefix + "SawRole"] = names.Contains("role");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
