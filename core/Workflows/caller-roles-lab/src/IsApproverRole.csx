using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// rule-schema-lab: true when the caller holds <c>rule-schema.approver</c>, read from the runtime-resolved
/// role set (<c>context.Caller</c>) — the gateway role header under the default provider, the identity
/// service under morph-idm. A provider outage fails the selection (503 Authorization:110006); it never
/// falls through to the fallback entry.
/// </summary>
public class IsApproverRole : ScriptBase, IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context) =>
        await context.Caller.HasRoleAsync("rule-schema.approver");
}
