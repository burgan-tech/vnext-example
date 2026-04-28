using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class GrandchildStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        result.grandchildStarted = true;
        if (data != null)
        {
            if (HasProperty(data, "testId"))
            {
                result.testId = data.testId;
            }
            if (HasProperty(data, "parentData"))
            {
                result.parentData = data.parentData;
            }
            if (HasProperty(data, "childStarted"))
            {
                result.childStarted = data.childStarted;
            }
        }
        LogInformation("GrandchildStartMapping: grandchildStarted set");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
