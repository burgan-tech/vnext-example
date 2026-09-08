using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Extension executed by the PARTNER runtime. When the core parent's data function is called with
/// ?extensions=xd-child-ext while it sits in the cross-domain SubFlow state, this payload proves the
/// extension request descended into the partner instance (the data body itself stays the parent's).
/// </summary>
public class XdChildExtMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        object? testId = null;
        data?.TryGetValue("testId", out testId);

        return Task.FromResult(new ScriptResponse
        {
            Key = "xd-child-ext",
            Data = new
            {
                source = "partner",
                childInstanceId = context.Instance?.Id,
                testId
            }
        });
    }
}
