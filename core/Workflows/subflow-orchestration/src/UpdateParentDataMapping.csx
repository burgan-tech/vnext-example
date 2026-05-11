using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class UpdateParentDataMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();

        if (HasProperty(data, "childStarted"))
            result.childStarted = data.childStarted;
        if (HasProperty(data, "grandchildFinished"))
            result.grandchildFinished = data.grandchildFinished;

        result.childUpdatedParent = true;
        result.updateParentAt = DateTime.UtcNow.ToString("o");

        LogInformation("UpdateParentDataMapping completed - updateData from child to parent");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
