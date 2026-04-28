using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class ChildStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        result.childStarted = true;
        if (data != null)
        {
            if (HasProperty(data, "parentData"))
            {
                result.parentData = data.parentData;
            }
            if (HasProperty(data, "testId"))
            {
                result.testId = data.testId;
            }
        }
        LogInformation("ChildStartMapping: childStarted set");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
