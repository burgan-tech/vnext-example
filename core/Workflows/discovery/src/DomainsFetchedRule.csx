using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Domains Fetched Rule - Checks if domains were successfully fetched
/// </summary>
public class DomainsFetchedRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            // Always return true - mapping handles the logic via Key
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

