using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Task 13 (GetInstanceData) reading THIS instance WITHOUT task headers, then copying protected fields into unguarded
/// ones: <c>maskedExceptAuditor</c> (x-masking), <c>auditTrail</c> (x-roles, auditor only), <c>customer.contact.phone</c>
/// (x-roles-guarded parent + hash) and <c>vault.email</c> (x-encryption encrypt).
/// <para>
/// A trigger task carries its caller's credential (sub, act_sub, position, client_id, role) wherever its own mapping sets
/// none. This mapping sets none, so the read is the transition caller's: a maker sees masks, pruned fields and the
/// encrypted value as its token; an auditor sees them in clear. The script's own view (<c>context.Instance.Data</c>)
/// always shows the token; <c>context.Instance.DecryptAsync(path)</c> opens this instance's own field.
/// </para>
/// <para>
/// The token itself is NOT copied: the write guard refuses an encrypted value at any path other than its own
/// (<c>EncryptedValueReservedException</c>), so <c>mirroredVaultEmail</c> records <c>"&lt;token&gt;"</c> instead.
/// </para>
/// </summary>
public class SelfReadMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var get = task as GetInstanceDataTask ?? throw new InvalidOperationException("Task must be a GetInstanceDataTask");
        get.SetInstance(context.Instance.Id.ToString());
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // Body = { isSuccess, data: <GetInstanceDataOutput { data, extensions, ... }>, ... }
        var dto = Get(context.Body, "data") ?? context.Body;
        var data = Get(dto, "data") ?? dto;

        var masked = Get(data, "maskedExceptAuditor")?.ToString();
        var audit = Get(data, "auditTrail")?.ToString();
        // Nested, x-roles-guarded parent (customer.contact, maker denied) + x-encryption hash child.
        var phone = Get(Get(Get(data, "customer"), "contact"), "phone")?.ToString();
        // x-encryption "encrypt": the task read gets the token unless its caller is exempt; the script's own view
        // (context.Instance.Data). DecryptAsync opens the instance's OWN field by path; the value the task returned,
        // handed to it as if it were a path, is not a path of this instance and stays closed.
        var vaultEmail = Get(Get(data, "vault"), "email")?.ToString();
        var scriptVaultEmail = Get(Get(context.Instance?.Data, "vault"), "email")?.ToString();
        var decrypted = context.Instance is null ? null : await context.Instance.DecryptAsync("vault.email");
        var decryptedTaskValue = context.Instance is null ? null : await context.Instance.DecryptAsync(vaultEmail ?? "vault.none");
        LogInformation($"SelfReadMapping: copied masked={(masked == null ? "<absent>" : "present")} audit={(audit == null ? "<absent>" : "present")}");

        return new ScriptResponse
        {
            Data = new
            {
                mirroredMasked = masked ?? "<absent>",
                mirroredAuditTrail = audit ?? "<absent>",
                mirroredPhone = phone ?? "<absent>",
                mirroredVaultEmail = vaultEmail == null ? "<absent>"
                    : vaultEmail.StartsWith("ENCRYPTED:AES256:", StringComparison.Ordinal) ? "<token>" : vaultEmail,
                // A token may not be written to another field (the write funnel refuses it): record a marker.
                scriptSawVaultEmail = Marker(scriptVaultEmail),
                decryptedVaultEmail = decrypted ?? "<null>",
                decryptedTaskValue = decryptedTaskValue ?? "<null>"
            }
        };
    }

    private static string Marker(string? value) =>
        value == null ? "<absent>" : value.StartsWith("ENCRYPTED:AES256:", StringComparison.Ordinal) ? "<token>" : value;

    private static object? Get(object? node, string name)
    {
        if (node is IDictionary<string, object> dict)
            foreach (var kv in dict)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
}
