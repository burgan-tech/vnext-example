using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

// Marks the CHILD's data. A reader that sees `childMarker` is reading the child instance.
public class SdsChildStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        result.childMarker = "child";
        result.childCounter = 0;
        if (data != null)
        {
            if (HasProperty(data, "parentMarker"))
            {
                result.parentMarker = data.parentMarker;
            }
            if (HasProperty(data, "labRef"))
            {
                result.labRef = data.labRef;
            }
        }
        LogInformation("SdsChildStartMapping: childMarker set");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
