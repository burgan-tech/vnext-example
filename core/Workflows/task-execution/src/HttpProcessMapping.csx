using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

public class HttpProcessMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = (task as HttpTask)!;
        var data = context.Instance.Data;
        string baseUrl = GetConfigValue("MocklabBaseUrl");
        httpTask.Url = httpTask.Url.Replace("{MocklabBaseUrl}", baseUrl);
        var body = new { source = "task-execution-test", timestamp = DateTime.UtcNow.ToString("o") };
        httpTask.SetBody(body);
        return new ScriptResponse();
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        var taskResponse = context.Body;
        var responseBody = taskResponse.data;
        dynamic result = new ExpandoObject();
        if (HasProperty(data, "testId")) result.testId = data.testId;
        result.httpTaskCompleted = true;
        result.httpStatusCode = taskResponse.statusCode;
        result.httpIsSuccess = taskResponse.isSuccess;
        if (responseBody != null && HasProperty(responseBody, "processId"))
            result.processId = responseBody.processId;
        LogInformation($"HttpProcessMapping completed, status={taskResponse.statusCode}");
        return new ScriptResponse { Data = result };
    }
}
