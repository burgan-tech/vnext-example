using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// role-matrix-summary — an instance-scoped custom function that reports a small digest of the
/// instance it is called on.
/// <para>
/// The function definition declares <c>roles</c>, but the runtime no longer enforces them when the
/// function is invoked: custom-function invocation is the middle tier's boundary, not the engine's.
/// This mapping therefore does no role check of its own either — it is deliberately readable by any
/// caller so that the tests can prove execution succeeds while
/// <c>authorize?functionKey=role-matrix-summary</c> answers 403 for the same caller.
/// </para>
/// <para>
/// It echoes back the roles it observed on the request. That is the only place in this fixture where
/// the caller's resolved role set is visible from the outside, which is what makes it useful once the
/// morph-idm provider is switched on: the roles reported here must be the ones the provider returned,
/// not the ones the caller put in a header.
/// </para>
/// </summary>
public class RoleMatrixSummaryMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var instanceData = context.Instance?.Data as IDictionary<string, object>;

        dynamic payload = new ExpandoObject();
        var target = (IDictionary<string, object>)payload;

        target["caseRef"] = Read(instanceData, "caseRef");
        target["decision"] = Read(instanceData, "decision");
        target["noteMarks"] = ReadInt(instanceData, "noteMarks");
        target["observedRole"] = HeaderValue(context, "role");
        target["observedPosition"] = HeaderValue(context, "position");
        target["executed"] = true;

        dynamic response = new ExpandoObject();
        ((IDictionary<string, object>)response)["data"] = payload;

        LogInformation("RoleMatrixSummaryMapping: summary produced for the role-matrix-lab instance");
        return Task.FromResult(new ScriptResponse { Data = response });
    }

    private static string Read(IDictionary<string, object> data, string key)
    {
        if (data != null && data.TryGetValue(key, out var value) && value != null)
            return value.ToString();
        return string.Empty;
    }

    private static int ReadInt(IDictionary<string, object> data, string key)
    {
        var raw = Read(data, key);
        return int.TryParse(raw, out var number) ? number : 0;
    }

    private static string HeaderValue(ScriptContext context, string name)
    {
        var headers = context.Headers;
        if (headers != null && headers.TryGetValue(name, out var value) && value != null)
            return value.ToString();
        return string.Empty;
    }
}
