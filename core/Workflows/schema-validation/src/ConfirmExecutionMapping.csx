using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class ConfirmExecutionMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;

        var hasConfirmed = HasProperty(data, "confirmed");
        var hasConfirmedBy = HasProperty(data, "confirmedBy");
        var hasOrderId = HasProperty(data, "orderId");
        var hasInternalNote = HasProperty(data, "internalNote");
        LogInformation($"ConfirmExecutionMapping: hasConfirmed={hasConfirmed}, hasConfirmedBy={hasConfirmedBy}, hasOrderId={hasOrderId}, hasInternalNote={hasInternalNote}");

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

        result.confirmed = HasProperty(data, "confirmed") ? data.confirmed : true;
        result.confirmedBy = HasProperty(data, "confirmedBy")
            ? data.confirmedBy
            : "system-confirmed";

        result.status = "confirmed";
        result.updatedAt = DateTime.UtcNow.ToString("o");

        LogInformation($"ConfirmExecutionMapping executed, status=confirmed, confirmedBy={result.confirmedBy}");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
