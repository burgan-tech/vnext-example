using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// start-rollback-lab: the start mapping of the root and the child flow. It throws when the start
/// payload carries no <c>customerId</c>. The runtime inserts the instance row before it runs this
/// mapping, so a throw here is a start that fails AFTER the row exists; the start must leave no
/// instance behind.
/// </summary>
public class RequireCustomerStartMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context)
    {
        var body = context.Body;
        if (body == null || !HasProperty(body, "customerId"))
            throw new InvalidOperationException("start-rollback-lab: the start payload has no customerId");

        dynamic data = new ExpandoObject();
        data.customerId = body.customerId;
        data.startMapped = true;
        return Task.FromResult<dynamic>(data);
    }
}
