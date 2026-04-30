using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

public class TriggerTransitionMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var directTrigger = (task as DirectTriggerTask)!;
        var data = context.Instance.Data;

        if (HasProperty(data, "startedInstanceId"))
        {
            var targetId = data.startedInstanceId?.ToString();
            directTrigger.SetInstance(targetId!);
            LogInformation($"TriggerTransitionMapping InputHandler: SetInstance {targetId}");
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testId")) result.testId = data.testId;
        if (HasProperty(data, "startedAt")) result.startedAt = data.startedAt;
        if (HasProperty(data, "httpTaskCompleted")) result.httpTaskCompleted = data.httpTaskCompleted;
        if (HasProperty(data, "httpStatusCode")) result.httpStatusCode = data.httpStatusCode;
        if (HasProperty(data, "httpIsSuccess")) result.httpIsSuccess = data.httpIsSuccess;
        if (HasProperty(data, "processId")) result.processId = data.processId;
        if (HasProperty(data, "scriptProcessed")) result.scriptProcessed = data.scriptProcessed;
        if (HasProperty(data, "scriptProcessedAt")) result.scriptProcessedAt = data.scriptProcessedAt;
        if (HasProperty(data, "timerStartedAt")) result.timerStartedAt = data.timerStartedAt;
        if (HasProperty(data, "timerExpectedSeconds")) result.timerExpectedSeconds = data.timerExpectedSeconds;
        if (HasProperty(data, "startFlowCompleted")) result.startFlowCompleted = data.startFlowCompleted;
        if (HasProperty(data, "startFlowIsSuccess")) result.startFlowIsSuccess = data.startFlowIsSuccess;
        if (HasProperty(data, "startedInstanceId")) result.startedInstanceId = data.startedInstanceId;
        if (HasProperty(data, "getInstanceDataCompleted")) result.getInstanceDataCompleted = data.getInstanceDataCompleted;
        if (HasProperty(data, "getInstanceDataIsSuccess")) result.getInstanceDataIsSuccess = data.getInstanceDataIsSuccess;
        if (HasProperty(data, "remoteInstanceData")) result.remoteInstanceData = data.remoteInstanceData;

        result.taskResults = new ExpandoObject();
        if (HasProperty(data, "taskResults"))
        {
            if (HasProperty(data.taskResults, "notification")) result.taskResults.notification = data.taskResults.notification;
            if (HasProperty(data.taskResults, "triggerTransition")) result.taskResults.triggerTransition = data.taskResults.triggerTransition;
            if (HasProperty(data.taskResults, "subprocess")) result.taskResults.subprocess = data.taskResults.subprocess;
            if (HasProperty(data.taskResults, "getInstances")) result.taskResults.getInstances = data.taskResults.getInstances;
        }

        result.taskResults.triggerTransition = new ExpandoObject();
        result.taskResults.triggerTransition.completed = true;
        result.taskResults.triggerTransition.executedAt = DateTime.UtcNow.ToString("o");

        LogInformation("TriggerTransitionMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
