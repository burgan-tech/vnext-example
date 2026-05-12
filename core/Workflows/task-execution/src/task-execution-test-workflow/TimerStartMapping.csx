using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class TimerStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        var now = DateTime.UtcNow;

        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testId")) result.testId = data.testId;
        if (HasProperty(data, "startedAt")) result.startedAt = data.startedAt;
        if (HasProperty(data, "httpTaskCompleted")) result.httpTaskCompleted = data.httpTaskCompleted;
        if (HasProperty(data, "httpStatusCode")) result.httpStatusCode = data.httpStatusCode;
        if (HasProperty(data, "httpIsSuccess")) result.httpIsSuccess = data.httpIsSuccess;
        if (HasProperty(data, "processId")) result.processId = data.processId;
        if (HasProperty(data, "scriptProcessed")) result.scriptProcessed = data.scriptProcessed;
        if (HasProperty(data, "scriptProcessedAt")) result.scriptProcessedAt = data.scriptProcessedAt;

        // timerStartedAt: timer-wait-state'e girildigi an. Scheduled transition (Timer Task tip 9)
        // 3 sn sonra start-flow-state'e gecirir; testler human-task-state'te attributes okuyup
        // (DateTime.UtcNow - timerStartedAt) >= ~3 sn olmasini dogrular -> Timer Task gercekten beklemis demektir.
        result.timerStartedAt = now.ToString("o");
        result.timerExpectedSeconds = 3;
        result.timerCompleted = false;

        LogInformation($"TimerStartMapping: timerStartedAt={result.timerStartedAt}, timerExpectedSeconds=3");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
