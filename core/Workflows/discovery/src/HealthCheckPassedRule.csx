using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Health Check Passed Rule - Checks if health check passed
/// </summary>
public class HealthCheckPassedRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return false;

            var healthCheck = context.Instance.Data.healthCheck;
            
            if (healthCheck == null)
                return false;

            var isHealthy = healthCheck.isHealthy;
            if (isHealthy == null)
                return false;

            return isHealthy == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

