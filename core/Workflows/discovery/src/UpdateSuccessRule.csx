using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Update Success Rule - Checks if domain update was successful
/// </summary>
public class UpdateSuccessRule : IConditionMapping
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

            // Check if update was successful and operation is "update"
            return domainRegistration.success == true && 
                   domainRegistration.operation?.ToString() == "update";
        }
        catch (Exception)
        {
            return false;
        }
    }
}

