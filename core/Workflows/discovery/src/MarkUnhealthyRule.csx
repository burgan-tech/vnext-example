using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Mark Unhealthy Rule - Checks if domain should be marked as unhealthy
/// This rule checks if health URL is missing or health check failed
/// </summary>
public class MarkUnhealthyRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return true; // If no data, consider it unhealthy

            // Check if health URL retrieval failed
            var healthUrlRetrieved = context.Instance.Data.healthUrlRetrieved;
            if (healthUrlRetrieved == false)
                return true;

            // Check if health check failed
            var healthCheck = context.Instance.Data.healthCheck;
            if (healthCheck != null)
            {
                var isHealthy = healthCheck.isHealthy;
                if (isHealthy == false)
                    return true;
            }

            // Check if markUnhealthy flag is explicitly set
            var markUnhealthy = context.Instance.Data.markUnhealthy;
            if (markUnhealthy == true)
                return true;

            // If none of the unhealthy conditions are met, don't transition
            return false;
        }
        catch (Exception)
        {
            return true; // On error, consider it unhealthy
        }
    }
}

