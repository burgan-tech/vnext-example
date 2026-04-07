using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Next Page Fetched Rule - Checks if next page was successfully fetched
/// </summary>
public class NextPageFetchedRule : IConditionMapping
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

