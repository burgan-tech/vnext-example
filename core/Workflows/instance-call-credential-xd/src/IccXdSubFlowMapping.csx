using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-xd icc-xd-parent (core), states icc-xd-p-subflow and icc-xd-p-nest: start a SubFlow
/// child in the PARTNER domain. The start data's <c>variant</c> picks the mapping headers:
/// <list type="bullet">
///   <item><c>none</c> — no Headers: the child must see the caller's credential (XC-01, XC-07, XC-06 control).</item>
///   <item><c>blank</c> — <c>sub = ""</c> (a string dictionary): no <c>sub</c> may reach the child (XC-02, XC-06).</item>
/// </list>
/// The child key is <c>{parentId}-sub</c>; the start data's <c>coreTargetId</c> (the nested case's core target) is
/// handed to the child. OutputHandler copies what the child recorded — its <c>seen</c>, its id and, for the nested
/// child, the results of its two calls back into core — into the parent.
/// <para>No anonymous type in this file: an anonymous type in a SubFlow mapping script breaks its output handler.</para>
/// </summary>
public class IccXdSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var variant = Str(data, "variant") ?? "none";

        dynamic input = new ExpandoObject();
        input.mode = "record";
        input.coreTargetId = Str(data, "coreTargetId");

        var response = new ScriptResponse { Key = context.Instance.Id + "-sub", Data = input };
        switch (variant)
        {
            case "none":
                break;
            case "blank":
                response.Headers = new Dictionary<string, string> { ["sub"] = "" };
                break;
            default:
                throw new InvalidOperationException($"instance-call-credential-xd: unknown subflow variant '{variant}'");
        }

        return Task.FromResult(response);
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        dynamic output = new ExpandoObject();
        output.childSeen = Get(body, "seen");
        output.childId = Get(body, "selfId");
        output.backTrigger = Get(body, "backTrigger");
        output.backRead = Get(body, "backRead");
        output.childSettled = true;
        return Task.FromResult(new ScriptResponse { Data = output });
    }

    private static string Str(IDictionary<string, object> data, string name) =>
        data != null && data.TryGetValue(name, out var raw) && raw != null ? raw.ToString() : null;

    private static object Get(IDictionary<string, object> body, string name) =>
        body != null && body.TryGetValue(name, out var value) ? value : null;
}
