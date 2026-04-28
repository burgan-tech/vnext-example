using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

public class ErrorHttpMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = (task as HttpTask)!;
        var data = context.Instance.Data;
        string baseUrl = GetConfigValue("MocklabBaseUrl");
        httpTask.Url = httpTask.Url.Replace("{MocklabBaseUrl}", baseUrl);
        var body = new
        {
            source = "error-boundary-test",
            testId = HasProperty(data, "testId") ? data.testId : null,
            timestamp = DateTime.UtcNow.ToString("o")
        };
        httpTask.SetBody(body);
        return new ScriptResponse();
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        var taskResponse = context.Body;
        dynamic result = new ExpandoObject();
        if (HasProperty(data, "testId")) result.testId = data.testId;
        if (HasProperty(data, "errorTestStarted")) result.errorTestStarted = data.errorTestStarted;
        if (HasProperty(data, "startedAt")) result.startedAt = data.startedAt;
        result.httpErrorHandled = true;
        if (taskResponse != null)
        {
            result.httpStatusCode = taskResponse.statusCode;
            result.httpIsSuccess = taskResponse.isSuccess;
            if (taskResponse.data != null)
                result.httpResponseData = taskResponse.data;
        }
        LogInformation("ErrorHttpMapping: output after HTTP call (error boundary may have retried)");
        return new ScriptResponse { Data = result };
    }
}
