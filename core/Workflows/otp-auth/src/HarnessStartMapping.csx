using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>Test harness only: passes the start payload straight through to the SubFlow state.</summary>
public class HarnessStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse { Data = OtpAuthHelpers.Merge(context.Instance?.Data) });
    }
}
