using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class InitializeDataMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;

        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testPath"))
            result.testPath = data.testPath;
        else
            result.testPath = "pass";

        result.initialized = true;
        result.initializedAt = DateTime.UtcNow.ToString("o");
        result.stepLog = new[] { "initialize-state:onEntry" };

        LogInformation($"Lifecycle test initialized, testPath={result.testPath}");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
