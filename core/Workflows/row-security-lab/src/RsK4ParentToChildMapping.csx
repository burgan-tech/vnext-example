using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// row-security-lab K-4: the opted-in parent rs-k4-parent hands its testId to the child rs-k4-child
/// (no rowSecurity) so the test can list the child flow scoped to one run.
/// </summary>
public class RsK4ParentToChildMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        dynamic input = new ExpandoObject();
        ((IDictionary<string, object>)input)["testId"] =
            data != null && data.TryGetValue("testId", out var testId) ? testId : null;
        return Task.FromResult(new ScriptResponse { Data = input });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse { Data = new ExpandoObject() });
    }
}
