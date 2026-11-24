using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// faulted-recovery-lab: a mapping that does not compile (a variable used before it is declared). It fails the
/// hop every time, so neither the first attempt nor a retry can pass; the reserved cancel is the way out.
/// </summary>
public class BrokenMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context)
    {
        return Task.FromResult<dynamic>(notDeclaredAnywhere);
    }
}
