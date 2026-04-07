using System.Threading.Tasks;
using System.Collections.Generic;
using BBT.Workflow.Scripting;

/// <summary>
/// Aggregate Results Mapping - Combines results from current page
/// </summary>
public class AggregateResultsMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // No input handling needed
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // Get results from context
            var healthyDomains = context.Instance?.Data?.HealthyDomains as List<object> ?? new List<object>();
            var notHealthyDomains = context.Instance?.Data?.NotHealthyDomains as List<object> ?? new List<object>();

            // Aggregate results (already combined in CheckDomainHealthMapping)
            return new ScriptResponse
            {
                Key = "to-check-pagination",
                Data = new
                {
                    HealthyDomains = healthyDomains,
                    NotHealthyDomains = notHealthyDomains,
                    totalHealthy = healthyDomains.Count,
                    totalNotHealthy = notHealthyDomains.Count,
                    aggregatedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "aggregate", "results", "success" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "aggregate-error",
                Data = new
                {
                    error = "Exception during result aggregation",
                    errorDescription = ex.Message,
                    aggregatedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "aggregate", "exception", "error" }
            };
        }
    }
}

