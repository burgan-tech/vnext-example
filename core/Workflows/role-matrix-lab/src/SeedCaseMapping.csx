using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Seeds the three fields the master schema guards with x-roles, on the start transition.
/// <para>
/// The seeding has to happen here rather than from the start payload: field-level pruning is applied
/// on the way OUT, so the fixture needs values that exist for every caller and disappear only when
/// the reader's roles say so. A payload-driven seed would let a pruned read be confused with a
/// caller who simply never sent the field.
/// </para>
/// </summary>
public class SeedCaseMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var instanceData = context.Instance?.Data as IDictionary<string, object>;

        var caseRef = "case-unknown";
        if (instanceData != null && instanceData.TryGetValue("caseRef", out var raw) && raw != null)
        {
            caseRef = raw.ToString();
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;

        target["caseRef"] = caseRef;
        target["decisionNote"] = "seeded-decision-note";
        target["auditTrail"] = "seeded-audit-trail";

        LogInformation($"SeedCaseMapping: guarded fields seeded for {caseRef}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
