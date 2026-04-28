using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class FunctionOutputMapping : ScriptBase, IOutputHandler
{
    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var outputs = context.OutputResponse;
        if (outputs != null)
        {
            if (HasProperty(outputs, "vfeScriptTask"))
                result.scriptResult = outputs.vfeScriptTask;
            if (HasProperty(outputs, "vfeHttpTask"))
                result.httpResult = outputs.vfeHttpTask;
        }
        result.aggregated = true;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
