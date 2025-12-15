using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Initialize Contract Approval Workflow - Prepares initial data
/// </summary>
public class InitContractApprovalMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var inputData = context.Instance?.Data;
            var groupCode = inputData?.groupCode?.ToString() ?? "";
            
            return new ScriptResponse
            {
                Key = "init-success",
                Data = new
                {
                    groupCode = groupCode,
                    workflowStartedAt = DateTime.UtcNow,
                    documents = new object[] { },
                    currentDocumentIndex = 0,
                    readyCount = 0,
                    approvedCount = 0,
                    rejectedCount = 0,
                    totalDocuments = 0,
                    documentInstances = new object[] { },
                    status = "initialized"
                },
                Tags = new[] { "contract", "initialized" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "init-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

