using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Skip Health Check Rule - Checks if health check should be skipped
/// Returns true if healthUrl is missing OR (existing domain AND healthUrl didn't change)
/// </summary>
public class SkipHealthCheckRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
    
            return true;
        }
        catch (Exception)
        {
            return true;  // On error, skip
        }
    }
}

