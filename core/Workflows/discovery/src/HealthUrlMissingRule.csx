using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Health URL Missing Rule - Checks if health URL retrieval failed
/// </summary>
public class HealthUrlMissingRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return true; // If no data, consider it missing

            var healthUrlRetrieved = context.Instance.Data.healthUrlRetrieved;
            
            if (healthUrlRetrieved == null)
                return true; // If no retrieval result, consider it missing

            return healthUrlRetrieved == false;
        }
        catch (Exception)
        {
            return true; // On error, consider it missing
        }
    }
}

