using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class FunctionOutputMapping : IOutputHandler
{
    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var task1Result = context.OutputResponse?["vfeScriptTask"];
        var task2Result = context.OutputResponse?["vfeScriptTask2"];

        return Task.FromResult(new ScriptResponse
        {
            Key = "multi-task-output",
            Data = new
            {
                scriptResult = task1Result,
                secondResult = task2Result,
                aggregated = true,
                normalizedAt = DateTime.UtcNow
            }
        });
    }
}
