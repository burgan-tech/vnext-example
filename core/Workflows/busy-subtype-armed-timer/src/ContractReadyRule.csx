using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// The wait state's auto gate: satisfied once instance data carries <c>contractReady = true</c>.
/// <para>
/// Nothing but an updateData can flip it. The wait state is a Busy subtype, so the instance rests
/// Busy while the gate is closed, and the satisfied gate has to be carried forward by the
/// updateData's continuation handoff — the code path under test.
/// </para>
/// </summary>
public class ContractReadyRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var ready = false;
        if (data != null && data.TryGetValue("contractReady", out var raw) && raw != null)
        {
            bool.TryParse(raw.ToString(), out ready);
        }

        LogInformation($"ContractReadyRule: contractReady={ready}");
        return Task.FromResult(ready);
    }
}
