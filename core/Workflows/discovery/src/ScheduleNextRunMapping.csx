using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Schedule Next Run Mapping - Triggers domain-health-check workflow as subprocess for next execution
/// </summary>
public class ScheduleNextRunMapping : IMapping
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

            // Configure subprocess to trigger the same workflow
            subProcessTask.SetKey("domain-health-check");
            subProcessTask.SetVersion("1.0.0");

            // Prepare subprocess initialization data (empty for scheduled run)
            subProcessTask.SetBody(new { });

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "schedule-next-run-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // SubProcess is fire-and-forget, so we just return success
            return new ScriptResponse
            {
                Key = "scheduled",
                Data = new
                {
                    scheduledAt = DateTime.UtcNow,
                    nextRunIn = "1 hour"
                },
                Tags = new[] { "domain", "schedule", "health-check", "success" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "schedule-next-run-error",
                Data = new
                {
                    error = "Exception during scheduling",
                    errorDescription = ex.Message,
                    scheduledAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "schedule", "exception", "error" }
            };
        }
    }
}

