using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// faulted-recovery-lab: the updateData mapping of faulted-recovery-lab-nocancel, the same as morph-touch's
/// UpdateDataKeyOnlyMapping on rezervation: it writes nothing, so the transition can only assign the instance key
/// (the runtime takes it from the request's data.key).
/// </summary>
public class KeyOnlyMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context) => Task.FromResult<dynamic>(null);
}
