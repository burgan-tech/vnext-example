using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Initial Transition Rule - Always allows transition from initial state
/// This rule always returns true to allow automatic transition from initial state
/// </summary>
public class InitialTransitionRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            // Always allow transition from initial state
            // Initial state is the entry point, so transition should always proceed
            return true;
        }
        catch (Exception)
        {
            // On error, still allow transition to prevent workflow from getting stuck
            return true;
        }
    }
}

