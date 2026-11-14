using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>Moves that carry no input write nothing.</summary>
public class LpNoInputMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context) =>
        Task.FromResult<dynamic>(new Dictionary<string, object?>());
}
