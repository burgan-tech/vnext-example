using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// subflow-carried-headers-lab, SubFlow B: the probe for the parent's RESUME context.
/// <para>
/// InputHandler runs on the parent's resumed hop — the hop that child A's settlement started. Its
/// <c>context.Headers</c> is the resume request's headers: empty unless
/// Workflow:SubFlow:CarriedHeaders:ApplyToResume=true, in which case it is A's carried set. The value
/// of <c>sub</c> is forwarded into B's start data as <c>seenSub</c>; B finishes at once (mode auto)
/// and OutputHandler copies it back to the parent as <c>bSeenSub</c>.
/// </para>
/// </summary>
public class SfchChildBMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        string seen = null;
        if (context.Headers is IDictionary<string, string> typed)
        {
            foreach (var pair in typed)
            {
                if (string.Equals(pair.Key, "sub", StringComparison.OrdinalIgnoreCase)) seen = pair.Value;
            }
        }
        else if (context.Headers is IDictionary<string, object> loose)
        {
            foreach (var pair in loose)
            {
                if (string.Equals(pair.Key, "sub", StringComparison.OrdinalIgnoreCase)) seen = pair.Value?.ToString();
            }
        }

        dynamic input = new ExpandoObject();
        input.mode = "auto";
        input.seenSub = seen;
        return Task.FromResult(new ScriptResponse { Data = input });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        dynamic output = new ExpandoObject();
        output.bSeenSub = body != null && body.TryGetValue("seenSub", out var seen) ? seen : null;
        output.bSettled = true;
        return Task.FromResult(new ScriptResponse { Data = output });
    }
}
