using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class TimerEntryMapping : ScriptBase, IMapping
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
        if (HasProperty(data, "processEntryExecuted"))
            result.processEntryExecuted = data.processEntryExecuted;
        if (HasProperty(data, "processExitExecuted"))
            result.processExitExecuted = data.processExitExecuted;

        result.timerTriggered = true;
        result.timerTriggeredAt = DateTime.UtcNow.ToString("o");

        LogInformation("TimerEntryMapping: timer state entered");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
