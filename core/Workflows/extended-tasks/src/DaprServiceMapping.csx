using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class DaprServiceMapping : ScriptBase, IMapping
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
            if (HasProperty(data.taskResults, "notification")) result.taskResults.notification = data.taskResults.notification;
            if (HasProperty(data.taskResults, "triggerTransition")) result.taskResults.triggerTransition = data.taskResults.triggerTransition;
            if (HasProperty(data.taskResults, "getInstances")) result.taskResults.getInstances = data.taskResults.getInstances;
            if (HasProperty(data.taskResults, "subprocess")) result.taskResults.subprocess = data.taskResults.subprocess;
        }

        result.taskResults.daprService = new ExpandoObject();
        result.taskResults.daprService.completed = true;
        result.taskResults.daprService.executedAt = DateTime.UtcNow.ToString("o");

        var taskResponse = context.Body;
        if (taskResponse != null && HasProperty(taskResponse, "data"))
        {
            var responseData = taskResponse.data;
            if (responseData != null && HasProperty(responseData, "processId"))
                result.taskResults.daprService.processId = responseData.processId;
        }

        LogInformation("DaprServiceMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
