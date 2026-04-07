using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Health Check Triggered Rule - Checks if health check workflow was successfully triggered
/// Returns true if healthCheckTriggered == true
/// </summary>
public class HealthCheckTriggeredRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return false;

            var healthCheckTriggered = context.Instance.Data.healthCheckTriggered;
            return healthCheckTriggered == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

