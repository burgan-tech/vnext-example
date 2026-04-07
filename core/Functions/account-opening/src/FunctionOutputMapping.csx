using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Output mapping for multi-task function test.
/// Normalizes results from validate-account-policies (task 1) and
/// get-data-from-workflow (task 2) into a single response.
/// </summary>
public class FunctionOutputMapping : IOutputHandler
{
    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var policyResult = context.OutputResponse["validateAccountPolicies"].data;
        var instanceData = context.OutputResponse?["getDataFromWorkflow"].data;

        return Task.FromResult(new ScriptResponse
        {
            Key = "multi-task-function-output",
            Data = new
            {
                policyValidation = policyResult,
                instanceSnapshot = instanceData,
                normalizedAt = DateTime.UtcNow,
                instanceId = context.Instance?.Id,
                instanceKey = context.Instance?.Key,
                multi = true
            }
        });
    }
}
