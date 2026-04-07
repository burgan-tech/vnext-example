using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// To Check Pagination Rule - Always allows transition to pagination check
/// </summary>
public class ToCheckPaginationRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

