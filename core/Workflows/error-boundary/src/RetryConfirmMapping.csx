using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class RetryConfirmMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        if (HasProperty(data, "testId"))
            result.testId = data.testId;
        if (HasProperty(data, "errorTestStarted"))
            result.errorTestStarted = data.errorTestStarted;
        if (HasProperty(data, "startedAt"))
            result.startedAt = data.startedAt;
        if (HasProperty(data, "retryThrowAttemptCount"))
            result.retryThrowAttemptCount = data.retryThrowAttemptCount;
        if (HasProperty(data, "retryAttempt1Utc"))
            result.retryAttempt1Utc = data.retryAttempt1Utc;
        if (HasProperty(data, "retryAttempt2Utc"))
            result.retryAttempt2Utc = data.retryAttempt2Utc;
        if (HasProperty(data, "retryAttempt3Utc"))
            result.retryAttempt3Utc = data.retryAttempt3Utc;
        result.retryHandled = true;
        LogInformation("RetryConfirmMapping: retry boundary exhausted + ignore fallback confirmed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
