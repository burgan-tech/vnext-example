using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Domain Not Exists Rule - Checks if domain does not exist in workflow instance
/// </summary>
public class DomainNotExistsRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return true;  // If no data, assume domain doesn't exist

            var domainExists = context.Instance.Data.domainExists;
            
            if (domainExists == null)
                return true;  // If no check result, assume domain doesn't exist

            return domainExists == false;
        }
        catch (Exception)
        {
            return true;  // On error, assume domain doesn't exist
        }
    }
}



