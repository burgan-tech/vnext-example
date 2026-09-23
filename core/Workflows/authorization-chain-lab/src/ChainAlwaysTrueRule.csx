using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// The chain must assemble itself on start: an authorization test wants the instance already
// sitting at the deepest leaf, not a fixture the test has to drive hop by hop.
public class ChainAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(true);
}
