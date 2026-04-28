using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class ProcessExitMapping : ScriptBase, IMapping
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
        if (HasProperty(data, "initialized"))
            result.initialized = data.initialized;
        if (HasProperty(data, "initializedAt"))
            result.initializedAt = data.initializedAt;
        if (HasProperty(data, "transitionMappingExecuted"))
            result.transitionMappingExecuted = data.transitionMappingExecuted;
        if (HasProperty(data, "processEntryExecuted"))
            result.processEntryExecuted = data.processEntryExecuted;
        if (HasProperty(data, "processedAt"))
            result.processedAt = data.processedAt;

        result.processExitExecuted = true;
        result.exitedAt = DateTime.UtcNow.ToString("o");

        LogInformation("ProcessExitMapping executed");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
