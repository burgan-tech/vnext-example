using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Condition for check-time: state=active -> create-room-and-trigger.
/// </summary>
public class CanStartTransferMapping : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var absenceType = context.Instance?.Data?.absenceType?.ToString()?.Trim();
        return Task.FromResult(absenceType == "personel-leave");
    }
}