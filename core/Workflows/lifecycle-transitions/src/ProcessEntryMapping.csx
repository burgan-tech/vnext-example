using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class ProcessEntryMapping : ScriptBase, IMapping
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

        result.processEntryExecuted = true;
        result.processedAt = DateTime.UtcNow.ToString("o");

        var testPathForLog = HasProperty(result, "testPath") ? result.testPath?.ToString() : "(null)";
        LogInformation("ProcessEntryMapping executed, testPath=" + testPathForLog);

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
