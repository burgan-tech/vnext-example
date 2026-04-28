using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class ConfirmMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;

        dynamic result = new ExpandoObject();

        if (HasProperty(data, "orderId"))
            result.orderId = data.orderId;
        if (HasProperty(data, "customerName"))
            result.customerName = data.customerName;
        if (HasProperty(data, "amount"))
            result.amount = data.amount;
        if (HasProperty(data, "currency"))
            result.currency = data.currency;
        if (HasProperty(data, "internalNote"))
            result.internalNote = data.internalNote;
        if (HasProperty(data, "auditLog"))
            result.auditLog = data.auditLog;
        if (HasProperty(data, "confirmed"))
            result.confirmed = data.confirmed;
        if (HasProperty(data, "confirmedBy"))
            result.confirmedBy = data.confirmedBy;

        result.status = "confirmed";
        result.updatedAt = DateTime.UtcNow.ToString("o");

        LogInformation($"ConfirmMapping: confirmed by {result.confirmedBy}");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
