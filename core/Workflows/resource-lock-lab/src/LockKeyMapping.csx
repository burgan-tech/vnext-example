using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Resolves the resource-lock key for resource-lock-lab from instance data.
///
/// The key is whatever the test started the instance with (`lockKey`), so two instances contend by
/// being started with the SAME value and stay independent with different ones. Keeping the key in
/// instance data — rather than deriving it from something the runtime owns — is what lets one test
/// class drive both the contention and the no-contention cases.
/// </summary>
public class LockKeyMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context)
    {
        var data = context.Instance?.Data;

        string lockKey = null;
        if (HasProperty(data, "lockKey"))
            lockKey = data.lockKey?.ToString();

        // An empty key is a definition/test error, not a lock outcome: the runtime answers
        // Execution:200011 (ResourceLockKeyEmpty) and the transition fails. Returning a constant
        // here instead would hide that case behind a lock everyone shares.
        return Task.FromResult<dynamic>(string.IsNullOrWhiteSpace(lockKey) ? null : $"rl-lab:{lockKey}");
    }
}
