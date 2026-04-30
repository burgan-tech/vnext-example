using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

public class SubProcessMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // SubProcessTask (tip 14) fire-and-forget bir alt instance acar.
        // Body uzerinden subprocess'in instance data'sina parent referansi ve kaynak notu yaziyoruz;
        // testler bu alanlari subprocess GetInstance attributes'unda dogrulayabilsin.
        var subProcessTask = (task as SubProcessTask)!;

        var body = new
        {
            source = "task-execution-test",
            parentInstanceId = context.Instance.Id,
            note = "this is a subprocess started from task-execution-test-workflow"
        };
        subProcessTask.SetBody(body);

        LogInformation("SubProcessMapping InputHandler: body set with parentInstanceId/source/note");
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        var taskResponse = context.Body;
        var responseBody = taskResponse?.data;

        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testId")) result.testId = data.testId;
        if (HasProperty(data, "startedAt")) result.startedAt = data.startedAt;
        if (HasProperty(data, "httpTaskCompleted")) result.httpTaskCompleted = data.httpTaskCompleted;
        if (HasProperty(data, "httpStatusCode")) result.httpStatusCode = data.httpStatusCode;
        if (HasProperty(data, "httpIsSuccess")) result.httpIsSuccess = data.httpIsSuccess;
        if (HasProperty(data, "processId")) result.processId = data.processId;
        if (HasProperty(data, "scriptProcessed")) result.scriptProcessed = data.scriptProcessed;
        if (HasProperty(data, "scriptProcessedAt")) result.scriptProcessedAt = data.scriptProcessedAt;
        if (HasProperty(data, "crossWorkflowCompleted")) result.crossWorkflowCompleted = data.crossWorkflowCompleted;
        if (HasProperty(data, "crossWorkflowAt")) result.crossWorkflowAt = data.crossWorkflowAt;
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
            if (HasProperty(data.taskResults, "humanTask")) result.taskResults.humanTask = data.taskResults.humanTask;
        }

        result.taskResults.subprocess = new ExpandoObject();
        result.taskResults.subprocess.completed = true;
        result.taskResults.subprocess.executedAt = DateTime.UtcNow.ToString("o");

        // SubProcess yaniti runtime dokumanina gore data.id (yeni instance id) doner.
        // Eski yanitlarda data.instanceId gorulebilir; ikisini de guvenli sekilde dene.
        if (responseBody != null)
        {
            if (HasProperty(responseBody, "id"))
                result.subprocessInstanceId = responseBody.id;
            else if (HasProperty(responseBody, "instanceId"))
                result.subprocessInstanceId = responseBody.instanceId;
        }

        if (taskResponse != null && HasProperty(taskResponse, "isSuccess"))
            result.taskResults.subprocess.isSuccess = taskResponse.isSuccess;

        LogInformation("SubProcessMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
