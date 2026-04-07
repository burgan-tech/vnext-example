using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Health Check Failed Rule - Checks if health check failed
/// </summary>
public class HealthCheckFailedRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return true; // If no data, consider it failed

            var healthCheck = context.Instance.Data.healthCheck;
            
            if (healthCheck == null)
                return true; // If no health check result, consider it failed

            var isHealthy = healthCheck.isHealthy;
            if (isHealthy == null)
                return true; // If health status unknown, consider it failed

            return isHealthy == false;
        }
        catch (Exception)
        {
            return true; // On error, consider it failed
        }
    }
}

