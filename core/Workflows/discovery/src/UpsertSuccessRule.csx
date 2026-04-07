using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Upsert Success Rule - Checks if domain registration/update was successful
/// </summary>
public class UpsertSuccessRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return false;

            var domainRegistration = context.Instance.Data.domainRegistration;
            
            if (domainRegistration == null)
                return false;

            return domainRegistration.success == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}



