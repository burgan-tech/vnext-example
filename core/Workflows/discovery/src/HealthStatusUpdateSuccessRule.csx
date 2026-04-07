using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Health Status Update Success Rule - Checks if health status update was successful
/// </summary>
public class HealthStatusUpdateSuccessRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return false;

            var healthStatusUpdate = context.Instance.Data.healthStatusUpdate;
            
            if (healthStatusUpdate == null)
                return false;

            // Check if update was successful
            return healthStatusUpdate.success == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

