using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

// Marks the PARENT's data. A reader that sees `parentMarker` is reading the parent instance.
public class SdsParentStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        result.parentMarker = "parent";
        result.parentCounter = 0;
        if (data != null && HasProperty(data, "labRef"))
        {
            result.labRef = data.labRef;
        }
        LogInformation("SdsParentStartMapping: parentMarker set");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
