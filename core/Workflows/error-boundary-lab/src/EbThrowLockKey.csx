using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// eb-scripts (vnext#1268): a resource-lock key script that fails. The runtime answers
/// ResourceLockKeyResolutionFailed and faults the instance; the incident must keep the script's trace.
/// </summary>
public class EbThrowLockKey : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context)
    {
        return Task.FromResult<dynamic>(KeyOf(context));
    }

    private static string KeyOf(ScriptContext context) =>
        throw new InvalidOperationException("eb-scripts: the lock key script has no key");
}
