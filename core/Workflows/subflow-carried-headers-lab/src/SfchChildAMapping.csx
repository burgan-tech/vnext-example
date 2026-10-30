using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// subflow-carried-headers-lab, SubFlow A: starts sfch-child in the mode the test asked for
/// (<c>aMode</c>: manual | timer) and, on settlement, records what the child carried.
/// <para>
/// OutputHandler runs on the parent when child A completes or faults. <c>context.Headers</c> is the
/// carried set — the headers of the request that settled the child, re-filtered by the parent's
/// policy (Workflow:SubFlow:CarriedHeaders:ExposeToOutputMapping, default on). The test asserts the
/// four probes below: a value that should travel (<c>sub</c>, <c>x-carried-custom</c>), a value the
/// deny floor must strip (<c>authorization</c>) and one the framework exclusions must strip
/// (<c>x-subflow-depth</c>).
/// </para>
/// </summary>
public class SfchChildAMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var mode = data != null && data.TryGetValue("aMode", out var raw) && raw != null ? raw.ToString() : "manual";

        dynamic input = new ExpandoObject();
        input.mode = mode;
        return Task.FromResult(new ScriptResponse { Data = input });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        Dictionary<string, string> headers = Read((object)context.Headers);

        dynamic carried = new ExpandoObject();
        carried.sub = headers.TryGetValue("sub", out var sub) ? sub : null;
        carried.custom = headers.TryGetValue("x-carried-custom", out var custom) ? custom : null;
        carried.hasAuthorization = headers.ContainsKey("authorization");
        carried.hasDepth = headers.ContainsKey("x-subflow-depth");
        carried.hasToken = headers.ContainsKey("x-token-id");
        carried.count = headers.Count;
        carried.keys = string.Join(",", headers.Keys);

        dynamic output = new ExpandoObject();
        output.carried = carried;
        output.aSettled = true;
        return Task.FromResult(new ScriptResponse { Data = output });
    }

    private static Dictionary<string, string> Read(object headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is IDictionary<string, string> typed)
        {
            foreach (var pair in typed) result[pair.Key] = pair.Value;
        }
        else if (headers is IDictionary<string, object> loose)
        {
            foreach (var pair in loose) result[pair.Key] = pair.Value?.ToString();
        }

        return result;
    }
}
