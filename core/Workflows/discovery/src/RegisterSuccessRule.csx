using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Register Success Rule - Checks if domain registration was successful
/// </summary>
public class RegisterSuccessRule : IConditionMapping
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

            // Check if registration was successful and operation is "register"
            return domainRegistration.success == true && 
                   domainRegistration.operation?.ToString() == "register";
        }
        catch (Exception)
        {
            return false;
        }
    }
}

