using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-lab icc-parent, state icc-p-nest: starts icc-sub-nest (SubFlow S) with
/// <c>sub = ""</c>. That child starts an icc-target of its own (StartTask, no mapping headers) on its
/// start hop — the withheld <c>sub</c> must not come back one level down.
/// OutputHandler copies the child's record, its StartTask result (<c>nestCall</c>) and the grandchild's
/// key (<c>nestKey</c> = the child's <c>startKey</c>, <c>{childId}-start</c>) into the parent.
/// </summary>
public class IccNestSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic input = new ExpandoObject();
        input.mode = "record";
        return Task.FromResult(new ScriptResponse
        {
            Key = context.Instance.Id + "-nest",
            Data = input,
            Headers = new Dictionary<string, string> { ["sub"] = "" }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        dynamic output = new ExpandoObject();
        output.childSeen = Get(body, "seen");
        output.childId = Get(body, "selfId");
        output.nestKey = Get(body, "startKey");
        output.nestCall = Get(body, "call");
        output.childSettled = true;
        return Task.FromResult(new ScriptResponse { Data = output });
    }

    private static object Get(IDictionary<string, object> body, string name) =>
        body != null && body.TryGetValue(name, out var value) ? value : null;
}
