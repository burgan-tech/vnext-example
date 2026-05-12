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

        result.taskResults.subprocess = new ExpandoObject();
        result.taskResults.subprocess.completed = true;
        result.taskResults.subprocess.executedAt = DateTime.UtcNow.ToString("o");

        // SubProcessTask yanitinin gercek servis "data" govdesi parent attributes.subprocessData altinda saklanir
        // (integration test ve operasyonel inceleme icin). Ayrica subprocessInstanceId kolay erisim icin ayri yazilir.
        if (responseBody != null)
        {
            dynamic snapshot = new ExpandoObject();
            if (HasProperty(responseBody, "id")) snapshot.id = responseBody.id;
            if (HasProperty(responseBody, "instanceId")) snapshot.instanceId = responseBody.instanceId;
            if (HasProperty(responseBody, "state")) snapshot.state = responseBody.state;
            if (HasProperty(responseBody, "launched")) snapshot.launched = responseBody.launched;
            if (HasProperty(responseBody, "status")) snapshot.status = responseBody.status;
            if (HasProperty(responseBody, "statusCode")) snapshot.statusCode = responseBody.statusCode;
            result.subprocessData = snapshot;

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
