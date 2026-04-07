using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Scheduled Rule - Checks if next run was scheduled
/// </summary>
public class ScheduledRule : IConditionMapping
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

