using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// SubFlow Error Recovery Rule - Determines if we should proceed after SubFlow error
/// </summary>
public class SubFlowErrorRecoveryRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        // Check if there was an error in the SubFlow
        var data = context.Instance?.Data;
        
        // If there's a subFlow error result, this transition should fire
        if (data?.subFlowErrorResult != null)
        {
            return true;
        }
        
        return false;
    }
}

