using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Schedule Health Check Mapping - Triggers domain-health-check workflow as subprocess
/// </summary>
public class ScheduleHealthCheckMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var subProcessTask = task as SubProcessTask;
            if (subProcessTask == null)
            {
                throw new InvalidOperationException("Task must be a SubProcessTask");
            }

            var domainName = context.Instance?.Data?.domainName;
            var domainData = context.Instance?.Data?.domainData;
            var healthUrl = context.Instance?.Data?.healthUrl;

            // Configure subprocess
            subProcessTask.SetKey("domain-health-check");
            subProcessTask.SetVersion("1.0.0");

            // Prepare subprocess initialization data
            // Pass domain information to health check workflow
            subProcessTask.SetBody(new
            {
                domainName = domainName,
                healthUrl = healthUrl ?? domainData?.healthUrl,
                domainData = domainData
            });

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "schedule-health-check-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // SubProcess is fire-and-forget
            // Just track that it was initiated
            return new ScriptResponse
            {
                Key = "health-check-triggered",
                Data = new
                {
                    healthCheckTriggered = true,
                    triggeredAt = DateTime.UtcNow,
                    status = "HEALTH_CHECK_SUBPROCESS_LAUNCHED"
                },
                Tags = new[] { "domain", "health-check", "triggered", "subprocess", "success" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "health-check-trigger-exception",
                Data = new
                {
                    healthCheckTriggered = false,
                    error = "Exception during health check trigger",
                    errorDescription = ex.Message,
                    triggeredAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "health-check", "exception", "error" }
            };
        }
    }
}
