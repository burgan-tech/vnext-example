using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-lab icc-parent, state icc-p-subflow-anon: starts icc-sub with
/// <c>Headers = new { sub = "" }</c> — an anonymous object, one of the documented readable shapes. No
/// <c>sub</c> may reach the child; <c>act_sub</c> comes from the caller. Child key <c>{parentId}-anon</c>.
/// <para>
/// Kept in its own file on purpose. The runtime compiles a SubFlow OUTPUT mapping with
/// <c>CompileToInstanceAsync&lt;object&gt;</c> and takes the first type of the script assembly assignable to
/// <c>object</c> — the compiler-generated anonymous type, not this class — so the output handler fails
/// (<c>Instance:100030</c>, "is a generic type definition") and the parent faults. Sharing this file with the
/// other variants would fault those too. See the scenario README, "Runtime findings".
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
