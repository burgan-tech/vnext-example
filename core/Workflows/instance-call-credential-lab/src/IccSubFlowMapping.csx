using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-lab icc-parent, state icc-p-subflow: starts the icc-sub child (SubFlow S)
/// with the <c>Headers</c> shape the parent's start data names in <c>variant</c>:
/// <list type="bullet">
///   <item><c>none</c> — no Headers at all: the child must see the caller's credential.</item>
///   <item><c>dict-blank</c> — a string dictionary with <c>sub = ""</c>: no <c>sub</c> reaches the child.</item>
///   <item><c>other</c> — <c>sub</c> = the start data's <c>otherSub</c> (a GUID): the child sees that one.</item>
///   <item><c>string</c> — <c>Headers = "sub="</c>: unreadable, the SubFlow start must fail before a child exists.</item>
/// </list>
/// The anonymous-object shape (<c>new { sub = "" }</c>) lives in IccSubFlowAnonMapping, alone: an anonymous
/// type anywhere in a SubFlow mapping script breaks that mapping's OUTPUT handler (see the scenario README).
/// The child key is <c>{parentId}-sub</c>, so the test can GET the child (or prove it does not exist) by key.
/// OutputHandler copies what the child recorded (<c>seen</c>, <c>selfId</c>) into the parent.
/// </summary>
public class IccSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var variant = Str(data, "variant") ?? "none";

        dynamic input = new ExpandoObject();
        input.mode = "record";

        var response = new ScriptResponse { Key = context.Instance.Id + "-sub", Data = input };
        switch (variant)
        {
            case "none":
                break;
            case "dict-blank":
                response.Headers = new Dictionary<string, string> { ["sub"] = "" };
                break;
            case "other":
                response.Headers = new Dictionary<string, string> { ["sub"] = Str(data, "otherSub") };
                break;
            case "string":
                response.Headers = "sub=";
                break;
            default:
                throw new InvalidOperationException($"instance-call-credential-lab: unknown subflow variant '{variant}'");
        }

        return Task.FromResult(response);
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

    private static string Str(IDictionary<string, object> data, string name) =>
        data != null && data.TryGetValue(name, out var raw) && raw != null ? raw.ToString() : null;
}
