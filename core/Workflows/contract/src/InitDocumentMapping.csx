using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Initialize Document Process Mapping - Prepares document data for subprocess
/// </summary>
public class InitDocumentMapping : IMapping
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
            
            return new ScriptResponse
            {
                Key = "init-document-success",
                Data = new
                {
                    parentInstanceId = inputData?.parentInstanceId,
                    parentInstanceKey = inputData?.parentInstanceKey,
                    parentWorkflowKey = inputData?.parentWorkflowKey,
                    document = inputData?.document,
                    documentIndex = inputData?.documentIndex,
                    groupCode = inputData?.groupCode,
                    processStartedAt = DateTime.UtcNow,
                    renderStatus = "pending",
                    approvalStatus = "pending"
                },
                Tags = new[] { "contract", "document", "initialized" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "init-document-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

