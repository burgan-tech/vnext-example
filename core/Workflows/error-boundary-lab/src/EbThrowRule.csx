using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// eb-scripts (vnext#1268): an automatic-transition rule that fails. By design a failing rule counts
/// as "not satisfied": the instance stays where it is and no incident is recorded. Only the log
/// carries the exception.
/// </summary>
public class EbThrowRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) =>
        throw new InvalidOperationException("eb-scripts: the rule script failed");
}
