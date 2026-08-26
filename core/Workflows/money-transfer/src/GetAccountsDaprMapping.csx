using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

// Transport probe: this mapping backs get-accounts-dapr, a DaprService (type 3) task that hits the
// same MockLab endpoint as get-accounts (HTTP, type 6). AppId/MethodName/HttpVerb are static in the
// task config, so there is nothing dynamic to shape on input — this exists only to satisfy the
// workflow schema's onExecuteTask.mapping requirement and to report success/failure on output.
public class GetAccountsDaprMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var daprTask = task as DaprServiceTask;
            if (daprTask == null)
                throw new InvalidOperationException("Task must be a DaprServiceTask");

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "get-accounts-dapr-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var statusCode = (int?)(context.Body?.statusCode) ?? 200;
            bool succeeded = statusCode >= 200 && statusCode < 300;

            return Task.FromResult(new ScriptResponse
            {
                Key = succeeded ? "get-accounts-dapr-succeeded" : "get-accounts-dapr-failed",
                Tags = new[] { "money-transfer", "accounts", "dapr", succeeded ? "success" : "failure" }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "get-accounts-dapr-exception",
                Tags = new[] { "money-transfer", "accounts", "dapr", "exception" }
            });
        }
    }
}