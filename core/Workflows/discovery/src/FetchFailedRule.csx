using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Fetch Failed Rule - Checks if fetch failed
/// </summary>
public class FetchFailedRule : IConditionMapping
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

