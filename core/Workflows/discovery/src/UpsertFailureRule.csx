using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Upsert Failure Rule - Checks if domain registration/update failed
/// </summary>
public class UpsertFailureRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return true;  // If no data, consider it a failure

            var domainRegistration = context.Instance.Data.domainRegistration;
            
            if (domainRegistration == null)
                return true;  // If no registration result, consider it a failure

            return domainRegistration.success == false;
        }
        catch (Exception)
        {
            return true;  // On error, consider it a failure
        }
    }
}



