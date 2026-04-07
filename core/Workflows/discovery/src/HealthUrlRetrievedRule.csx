using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Health URL Retrieved Rule - Checks if health URL was successfully retrieved
/// </summary>
public class HealthUrlRetrievedRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return false;

            var healthUrlRetrieved = context.Instance.Data.healthUrlRetrieved;
            
            if (healthUrlRetrieved == null)
                return false;

            return healthUrlRetrieved == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

