using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Notify Document Ready Mapping - Notifies parent workflow that document is ready
/// </summary>
public class NotifyDocumentReadyMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var directTriggerTask = task as DirectTriggerTask;
            if (directTriggerTask == null)
            {
                throw new InvalidOperationException("Task must be a DirectTriggerTask");
            }

            // Configure target workflow
            directTriggerTask.SetDomain("core");
            directTriggerTask.SetFlow("contract-approval-workflow");
            directTriggerTask.SetTransitionName("document-ready");
            directTriggerTask.SetSync(true);
            
            // Set parent instance
            var parentInstanceId = context.Instance?.Data?.parentInstanceId?.ToString();
            var parentInstanceKey = context.Instance?.Data?.parentInstanceKey?.ToString();
            
            if (!string.IsNullOrEmpty(parentInstanceId))
            {
                directTriggerTask.SetInstance(parentInstanceId);
            }
            if (!string.IsNullOrEmpty(parentInstanceKey))
            {
                directTriggerTask.SetKey(parentInstanceKey);
            }

            // Prepare transition body - using Mockoon contract structure (contractId, contractName, contractType)
            var transitionBody = new
            {
                contractId = context.Instance?.Data?.document?.contractId,
                contractName = context.Instance?.Data?.document?.contractName,
                contractType = context.Instance?.Data?.document?.contractType,
                documentIndex = context.Instance?.Data?.documentIndex,
                subprocessInstanceId = context.Instance?.Id,
                subprocessInstanceKey = context.Instance?.Key,
                documentUrl = context.Instance?.Data?.documentUrl,
                documentId = context.Instance?.Data?.documentId,
                pageCount = context.Instance?.Data?.pageCount,
                readyAt = DateTime.UtcNow,
                status = "ready"
            };
            directTriggerTask.SetBody(transitionBody);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "notify-ready-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var response = context.Body;

            if (response?.isSuccess == true)
            {
                return new ScriptResponse
                {
                    Key = "notify-ready-success",
                    Data = new
                    {
                        parentNotified = true,
                        notifiedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "contract", "document-ready", "notified" }
                };
            }

            return new ScriptResponse
            {
                Key = "notify-ready-failed",
                Data = new
                {
                    parentNotified = false,
                    error = response?.errorMessage ?? "Failed to notify parent"
                }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "notify-ready-exception",
                Data = new { error = ex.Message }
            };
        }
    }
}

