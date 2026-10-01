using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Unconditional auto-transition gate for the subflow fault chain (eb-sf-root / -mid / -leaf).
/// Auto transitions must carry a rule; this one only exists to satisfy that.
/// </summary>
public class EbAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(true);
    }
}
