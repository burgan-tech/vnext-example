using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-lab icc-parent, state icc-p-subflow-anon: tries to start icc-sub with
/// <c>Headers = new { sub = "" }</c> — an anonymous object. The runtime accepts a SubFlow mapping's
/// <c>Headers</c> only as a string dictionary, an object dictionary / ExpandoObject or a JSON object, so this
/// start must FAIL before any child exists (<c>SubFlowInputMappingFailed</c>), exactly like a string.
/// <para>
/// Kept in its own file and state on purpose: an anonymous type anywhere in a SubFlow mapping script also
/// breaks that script's OUTPUT handler (it is compiled as <c>CompileToInstanceAsync&lt;object&gt;</c>, which
/// picks the compiler-generated anonymous type) — sharing the file would fault the other variants' parents.
/// </para>
/// </summary>
public class IccSubFlowAnonMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic input = new ExpandoObject();
        input.mode = "record";
        return Task.FromResult(new ScriptResponse
        {
            Key = context.Instance.Id + "-anon",
            Data = input,
            Headers = new { sub = "" }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        dynamic output = new ExpandoObject();
        output.childSeen = body != null && body.TryGetValue("seen", out var seen) ? seen : null;
        output.childId = body != null && body.TryGetValue("selfId", out var id) ? id : null;
        output.childSettled = true;
        return Task.FromResult(new ScriptResponse { Data = output });
    }
}
