using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Seeds the three fields the master schema guards with x-roles, on the start transition.
/// <para>
/// The seeding has to happen here rather than from the start payload: field-level pruning is applied
/// on the way OUT, so the fixture needs values that exist for every caller and disappear only when
/// the reader's roles say so. A payload-driven seed would let a pruned read be confused with a
/// caller who simply never sent the field.
/// </para>
/// </summary>
public class SeedCaseMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var instanceData = context.Instance?.Data as IDictionary<string, object>;

        var caseRef = "case-unknown";
        if (instanceData != null && instanceData.TryGetValue("caseRef", out var raw) && raw != null)
        {
            caseRef = raw.ToString();
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;

        target["caseRef"] = caseRef;
        target["decisionNote"] = "seeded-decision-note";
        target["auditTrail"] = "seeded-audit-trail";
        // x-masking fixtures (field-masking lab). Stored in clear; masked only on the way out.
        target["maskedForAll"] = "TR330006100519786457841326";
        target["maskedExceptAuditor"] = "12345678901";
        target["replacedNote"] = "internal note the caller must not see";
        target["hashedCustomerNo"] = "98765432109";
        // Nested / array / non-string exposure fixtures (x-roles, x-masking, x-encryption below the root).
        target["customer"] = new Dictionary<string, object>
        {
            ["name"] = "Ayşe Yılmaz",
            ["tckn"] = "10000000146",
            ["segment"] = "premium",
            ["contact"] = new Dictionary<string, object>
            {
                ["email"] = "ayse.yilmaz@example.com",
                ["phone"] = "+905321234567",
                ["address"] = new Dictionary<string, object>
                {
                    ["city"] = "İstanbul",
                    ["line1"] = "Levent Mah. Büyükdere Cad. No:1"
                }
            }
        };
        target["accounts"] = new List<object>
        {
            new Dictionary<string, object> { ["iban"] = "TR560001000000000000000001", ["balance"] = 1250.75 },
            new Dictionary<string, object> { ["iban"] = "TR560001000000000000000002", ["balance"] = 0 }
        };
        target["riskScore"] = 72;
        target["ownerNote"] = "owner-only note";
        target["tags"] = new List<object> { "vip", "kyc-done", "tr" };
        // x-encryption "encrypt" fixtures: written in plaintext here, stored as AES-256-GCM tokens by the runtime.
        target["vault"] = new Dictionary<string, object>
        {
            ["email"] = "vault-user@example.com",
            ["pin"] = "4321",
            ["label"] = "not encrypted"
        };

        LogInformation($"SeedCaseMapping: guarded fields seeded for {caseRef}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
