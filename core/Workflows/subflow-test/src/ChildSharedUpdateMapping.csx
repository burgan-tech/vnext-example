using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Child Shared Update Mapping - Processes data sent via child's shared transition (target $self).
/// Used to verify issue #425: child (subflow) can handle shared transitions while in its own subflow.
/// </summary>
public class ChildSharedUpdateMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ChildSharedUpdateMapping - Processing shared transition data on child (target $self)");

            var body = context.Body as System.Collections.Generic.IDictionary<string, object>;
            object m, v;
            var message = body != null && body.TryGetValue("message", out m) ? m?.ToString() : null;
            var value = body != null && body.TryGetValue("value", out v) ? v : null;

            return new ScriptResponse
            {
                Key = "child-shared-update-processed",
                Data = new
                {
                    sharedUpdateFromChild = new
                    {
                        receivedAt = DateTime.UtcNow,
                        message = message ?? "no message",
                        value = value,
                        workflowKey = context.Workflow?.Key,
                        instanceId = context.Instance?.Id,
                        note = "Child flow processed this while in subflow (issue #425 test)"
                    }
                },
                Tags = new[] { "subflow-test", "shared-transition", "child", "issue-425" }
            };
        }
        catch (Exception ex)
        {
            LogError("ChildSharedUpdateMapping - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "child-shared-update-error",
                Data = new { error = ex.Message }
            };
        }
    }
}
