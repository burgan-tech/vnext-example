using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// subflow-carried-headers-lab: the parent's automatic-transition condition on the hop that resumes
/// it after child A settled. True only when the resume context carries a non-empty <c>sub</c> header —
/// which happens only with Workflow:SubFlow:CarriedHeaders:ApplyToResume=true. Otherwise the default
/// automatic transition (resume-no-sub) wins.
/// </summary>
public class SfchResumeSawSubRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        string seen = null;
        if (context.Headers is IDictionary<string, string> typed)
        {
            foreach (var pair in typed)
            {
                if (string.Equals(pair.Key, "sub", StringComparison.OrdinalIgnoreCase)) seen = pair.Value;
            }
        }
        else if (context.Headers is IDictionary<string, object> loose)
        {
            foreach (var pair in loose)
            {
                if (string.Equals(pair.Key, "sub", StringComparison.OrdinalIgnoreCase)) seen = pair.Value?.ToString();
            }
        }

        return Task.FromResult(!string.IsNullOrEmpty(seen));
    }
}
