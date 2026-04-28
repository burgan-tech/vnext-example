using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class SchemaInitMapping : ScriptBase, IMapping
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

        result.status = "initialized";
        result.internalNote = "Created by integration test";
        result.auditLog = "Init at " + DateTime.UtcNow.ToString("o");

        LogInformation($"SchemaInitMapping: order={result.orderId}");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
