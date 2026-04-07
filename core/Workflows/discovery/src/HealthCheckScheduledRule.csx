using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Health Check Scheduled Rule - Checks if health check was successfully scheduled
/// </summary>
public class HealthCheckScheduledRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return false;

            var healthCheckSchedule = context.Instance.Data.healthCheckSchedule;
            
            if (healthCheckSchedule == null)
                return false;

            return healthCheckSchedule.success == true && healthCheckSchedule.scheduled == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}



