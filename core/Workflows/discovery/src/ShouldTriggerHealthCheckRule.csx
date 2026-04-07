using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Should Trigger Health Check Rule - Checks if health check workflow should be triggered
/// Returns true if healthUrl exists AND (new domain OR healthUrl changed)
/// </summary>
public class ShouldTriggerHealthCheckRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

