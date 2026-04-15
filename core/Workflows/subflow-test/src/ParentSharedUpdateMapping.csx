using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Parent Shared Update Mapping - Processes data sent via parent's shared transition (target $self).
/// Used to verify issue #425: parent flow can handle shared transitions while instance is in subflow.
/// </summary>
public class ParentSharedUpdateMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ParentSharedUpdateMapping - Processing shared transition data on parent (target $self)");

            // Transition body is the PATCH request body (e.g. { "message": "...", "value": ... })
            var body = context.Body as System.Collections.Generic.IDictionary<string, object>;
            object m, v;
            var message = body != null && body.TryGetValue("message", out m) ? m?.ToString() : null;
            var value = body != null && body.TryGetValue("value", out v) ? v : null;

            return new ScriptResponse
            {
                Key = "parent-shared-update-processed",
                Data = new
                {
                    sharedUpdateFromParent = new
                    {
                        receivedAt = DateTime.UtcNow,
                        message = message ?? "no message",
                        value = value,
                        workflowKey = context.Workflow?.Key,
                        instanceId = context.Instance?.Id,
                        note = "Parent flow processed this while in subflow (issue #425 test)"
                    }
                },
                Tags = new[] { "subflow-test", "shared-transition", "parent", "issue-425" }
            };
        }
        catch (Exception ex)
        {
            LogError("ParentSharedUpdateMapping - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "parent-shared-update-error",
                Data = new { error = ex.Message }
            };
        }
    }
}
