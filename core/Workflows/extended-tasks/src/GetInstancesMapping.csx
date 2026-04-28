using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class GetInstancesMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testId")) result.testId = data.testId;
        if (HasProperty(data, "initCompleted")) result.initCompleted = data.initCompleted;

        result.taskResults = new ExpandoObject();
        if (HasProperty(data, "taskResults"))
        {
            if (HasProperty(data.taskResults, "daprHttp")) result.taskResults.daprHttp = data.taskResults.daprHttp;
            if (HasProperty(data.taskResults, "daprService")) result.taskResults.daprService = data.taskResults.daprService;
            if (HasProperty(data.taskResults, "daprBinding")) result.taskResults.daprBinding = data.taskResults.daprBinding;
            if (HasProperty(data.taskResults, "daprPubSub")) result.taskResults.daprPubSub = data.taskResults.daprPubSub;
            if (HasProperty(data.taskResults, "notification")) result.taskResults.notification = data.taskResults.notification;
            if (HasProperty(data.taskResults, "triggerTransition")) result.taskResults.triggerTransition = data.taskResults.triggerTransition;
            if (HasProperty(data.taskResults, "subprocess")) result.taskResults.subprocess = data.taskResults.subprocess;
        }

        result.taskResults.getInstances = new ExpandoObject();
        result.taskResults.getInstances.completed = true;
        result.taskResults.getInstances.executedAt = DateTime.UtcNow.ToString("o");

        var taskResponse = context.Body;
        if (taskResponse != null && HasProperty(taskResponse, "data"))
        {
            result.taskResults.getInstances.responseReceived = true;
        }

        LogInformation("GetInstancesMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
