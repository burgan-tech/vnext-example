using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class TriggerScheduledPaymentsMapping :ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var subProcessTask = task as SubProcessTask;

        if (subProcessTask == null)
        {
            throw new InvalidOperationException("Task must be a SubProcessTask");
        }

        // Configure subprocess
        subProcessTask.SetDomain("core");
        subProcessTask.SetKey("scheduled-payments");
        subProcessTask.SetVersion("1.0.0");

        // Prepare subprocess initialization data
        // Pass relevant data from account-opening workflow to scheduled-payments
        subProcessTask.SetBody(new
        {
            userId = 1,
            amount = 12000,
            currency = "TL",
            frequency = "monthly",
            startDate = DateTime.UtcNow,
            endDate = "2026-10-01T09:02:38.201Z",
            paymentMethodId = "1",
            description = "Scheduled payment bootstrap",
            recipientId = "324324",
            isAutoRetry = false,
            maxRetries = 3
        });

        return Task.FromResult(new ScriptResponse
        {
            Data = context.Instance?.Data
        });
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var response = new ScriptResponse();

        // SubProcess is fire-and-forget
        // Just track that it was initiated
      
            response.Data = new
            {
                scheduledPaymentsInitiated = true,
                initiatedAt = DateTime.UtcNow,
                status = "SCHEDULED_PAYMENTS_SUBPROCESS_LAUNCHED"
            };

        return response;
    }
}

