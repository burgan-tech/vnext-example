using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Task 13 (GetInstanceData) reading THIS instance WITH an auditor credential in its own headers, then copying protected fields into unguarded
/// ones: <c>maskedExceptAuditor</c> (x-masking), <c>auditTrail</c> (x-roles, auditor only), <c>customer.contact.phone</c>
/// (x-roles-guarded parent + hash) and <c>vault.email</c> (x-encryption encrypt).
/// <para>
/// A trigger task is evaluated as its OWN credential — its mapping headers — never as the caller of the transition. This
/// mapping sets <c>role: morph-idm.auditor</c>, so the read sees what an auditor sees — whoever drove the transition. The engine's own view (<c>context.Instance.Data</c>) stays
/// plaintext.
/// </para>
/// </summary>
public class SelfReadAuditorMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var get = task as GetInstanceDataTask ?? throw new InvalidOperationException("Task must be a GetInstanceDataTask");
        get.SetInstance(context.Instance.Id.ToString());
        // The task's credential is what the developer puts in its input binding: here the auditor role.
        get.SetHeaders(new Dictionary<string, string?> { ["role"] = "morph-idm.auditor" });
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // Body = { isSuccess, data: <GetInstanceDataOutput { data, extensions, ... }>, ... }
        var dto = Get(context.Body, "data") ?? context.Body;
        var data = Get(dto, "data") ?? dto;

        var masked = Get(data, "maskedExceptAuditor")?.ToString();
        var audit = Get(data, "auditTrail")?.ToString();
        // Nested, x-roles-guarded parent (customer.contact, maker denied) + x-encryption hash child.
        var phone = Get(Get(Get(data, "customer"), "contact"), "phone")?.ToString();
        // x-encryption "encrypt": the system read and the engine's own script context both see plaintext.
        var vaultEmail = Get(Get(data, "vault"), "email")?.ToString();
        var scriptVaultEmail = Get(Get(context.Instance?.Data, "vault"), "email")?.ToString();
        LogInformation($"SelfReadAuditorMapping: copied masked={(masked == null ? "<absent>" : "present")} audit={(audit == null ? "<absent>" : "present")}");

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                mirroredMasked = masked ?? "<absent>",
                mirroredAuditTrail = audit ?? "<absent>",
                mirroredPhone = phone ?? "<absent>",
                mirroredVaultEmail = vaultEmail ?? "<absent>",
                // The script's own view shows the token; a token may not be written to another field.
                scriptSawVaultEmail = scriptVaultEmail == null ? "<absent>"
                    : scriptVaultEmail.StartsWith("ENCRYPTED:AES256:", StringComparison.Ordinal) ? "<token>" : scriptVaultEmail
            }
        });
    }

    private static object? Get(object? node, string name)
    {
        if (node is IDictionary<string, object> dict)
            foreach (var kv in dict)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
}
