using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Pass-through for data. It DOES forward the caller's role headers: SubflowStarter sends the child
// only the headers the input mapping returns, and the long-poll arm (order 75) pauses only when the
// TRIGGERING caller satisfies the effective long-poll roles. Without this forward a roles-armed
// long-poll in a SubFlow child never pauses, and nothing about the override would be observable.
public class OverrideLabSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic input = new ExpandoObject();
        input.startedByOverrideLab = true;

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "x-roles", "role", "user_reference", "x-device-id", "x-token-id" })
        {
            try
            {
                string? value = (string?)context.Headers[name];
                if (!string.IsNullOrEmpty(value)) headers[name] = value;
            }
            catch (Exception)
            {
                // header absent
            }
        }

        return Task.FromResult(new ScriptResponse { Data = input, Headers = headers });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
        => Task.FromResult(new ScriptResponse());
}
