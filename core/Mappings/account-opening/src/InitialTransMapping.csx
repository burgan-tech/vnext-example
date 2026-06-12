using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class InitialTransMapping : ITransitionMapping
{
    public async Task<dynamic> Handler(ScriptContext context)
    {
        dynamic output = new ExpandoObject();
        try
        {
            output.initial = new {
                session = context.Body.session,
                requestId = context.Headers?["x-request-id"] ?? Guid.NewGuid().ToString(),
                customer = context.Body.customer
            };
        

            return output;
        }
        catch (Exception ex)
        {
            return output;
        }
    }
}
