using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Same intentional failure as ThrowErrorMapping, but increments retryThrowAttemptCount
/// in InputHandler so each retry attempt is observable in instance data (before OutputHandler throws).
/// Also records ISO 8601 UTC stamps per attempt for backoff timing assertions.
/// </summary>
public class RetryThrowMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testId"))
            result.testId = data.testId;
        if (HasProperty(data, "errorTestStarted"))
            result.errorTestStarted = data.errorTestStarted;
        if (HasProperty(data, "startedAt"))
            result.startedAt = data.startedAt;

        var prior = 0;
        if (HasProperty(data, "retryThrowAttemptCount"))
        {
            try
            {
                prior = Convert.ToInt32(data.retryThrowAttemptCount);
            }
            catch
            {
                prior = 0;
            }
        }

        var next = prior + 1;
        result.retryThrowAttemptCount = next;

        var stamp = DateTime.UtcNow.ToString("o");
        if (next == 1)
            result.retryAttempt1Utc = stamp;
        else if (next == 2)
            result.retryAttempt2Utc = stamp;
        else if (next == 3)
            result.retryAttempt3Utc = stamp;

        LogInformation($"RetryThrowMapping InputHandler attempt {next}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        throw new InvalidOperationException("Intentional test error for error boundary");
    }
}
