using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// faulted-recovery-lab: the mapping of the manual transition, of updateData and of the reserved cancel.
/// The request decides how it ends, through <c>failWith</c>:
/// <list type="bullet">
/// <item><c>"throw"</c> — the mapping throws, before anything is written.</item>
/// <item><c>"schema"</c> — the mapping returns a field the closed master schema does not declare, so the
/// data write that follows it fails schema validation.</item>
/// <item>absent — the mapping returns <c>note</c> (when sent) and <c>mappedBy</c>.</item>
/// </list>
/// </summary>
public class FailOnDemandMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context)
    {
        var body = context.Body;
        var failWith = body != null && HasProperty(body, "failWith") ? body.failWith?.ToString() : null;

        if (failWith == "throw")
            throw new InvalidOperationException("faulted-recovery-lab: the request asked the mapping to throw");

        dynamic data = new ExpandoObject();
        data.mappedBy = "fail-on-demand";
        if (body != null && HasProperty(body, "note"))
            data.note = body.note?.ToString();
        if (failWith == "schema")
            data.notInTheMasterSchema = true;

        return Task.FromResult<dynamic>(data);
    }
}
