using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Task 13 (GetInstanceData) reading THIS instance, then copying protected fields into unguarded
/// ones: <c>maskedExceptAuditor</c> (x-masking), <c>auditTrail</c> (x-roles, auditor only) and
/// <c>vault.email</c> (x-encryption encrypt — also read straight from the script context).
/// <para>
/// A trigger task reads under the engine's own identity (the runtime's SystemRead flag): no x-roles
/// pruning, no x-masking. The copies must therefore hold the STORED values whatever roles the caller
/// of the transition carried. Before the flag, the read ran the x-roles filter with the caller's ambient
/// roles, so a Maker-driven copy of auditTrail came back empty — the red baseline this fixture records.
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
        LogInformation($"SelfReadMapping: copied masked={(masked == null ? "<absent>" : "present")} audit={(audit == null ? "<absent>" : "present")}");

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                mirroredMasked = masked ?? "<absent>",
                mirroredAuditTrail = audit ?? "<absent>",
                mirroredPhone = phone ?? "<absent>",
                mirroredVaultEmail = vaultEmail ?? "<absent>",
                scriptSawVaultEmail = scriptVaultEmail ?? "<absent>"
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
