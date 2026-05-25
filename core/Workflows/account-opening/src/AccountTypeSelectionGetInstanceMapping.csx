using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

public class AccountTypeSelectionGetInstanceMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Configure task input here
        return new ScriptResponse();
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // Process task response
        return new ScriptResponse
        {
            Data = new
            {
                // Map output data here
            }
        };
    }
}