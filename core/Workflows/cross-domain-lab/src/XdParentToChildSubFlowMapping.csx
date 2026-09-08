using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// SubFlow mapping for xd-subflow (core parent -> partner xd-child).
/// InputHandler builds the child's start payload; OutputHandler merges the child's final data back
/// into the parent and stamps childCompleted so the tests (and the auto transition) can see it.
/// </summary>
public class XdParentToChildSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        dynamic childInput = new ExpandoObject();
        childInput.parentInstanceId = context.Instance?.Id;
        childInput.parentDomain = "core";

        if (data != null && data.TryGetValue("testId", out var testId))
        {
            childInput.testId = testId;
        }

        LogInformation("XdParentToChildSubFlowMapping: child input prepared for partner/xd-child");
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;

        if (context.Instance?.Data is IDictionary<string, object> inst)
        {
            foreach (var kv in inst) target[kv.Key] = kv.Value;
        }

        // Whitelist, not a blanket merge: the body is the PARTNER child's final data. Copying it
        // wholesale would let a foreign domain overwrite parent keys and would bypass any partner
        // x-roles field filtering. Only the fields the parent actually needs cross the boundary.
        if (context.Body is IDictionary<string, object> body)
        {
            if (body.TryGetValue("approvedBy", out var approvedBy)) target["childApprovedBy"] = approvedBy;
            if (body.TryGetValue("testId", out var childTestId)) target["childTestId"] = childTestId;
        }

        target["childCompleted"] = true;
        LogInformation("XdParentToChildSubFlowMapping: partner child output merged into parent");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
