using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Always True Rule - Used for auto transitions that should always proceed (repro: invalidated when onExecutionTask target is self)
/// </summary>
public class AlwaysTrueRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        return context.Instance.Data.condition == 1;
    }
}
