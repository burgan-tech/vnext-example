using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Minimal SubFlow mapping for implicit-start-lab-parent. The scenario is about WHERE the child is
/// born (the runtime's implicit <c>$start</c> source state, because the child declares no Initial
/// state), not about data, so only a marker crosses the boundary in each direction.
/// </summary>
/// <remarks>
/// It exists because <c>subFlow.mapping</c> is required by the schema. Keeping it trivial is
/// deliberate: a mapping that moved real data would give a failing test a second possible cause.
/// </remarks>
public class ImplicitStartLabSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic childInput = new ExpandoObject();
        childInput.startedByImplicitStartLabParent = true;
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        result.implicitStartChildSettled = true;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
