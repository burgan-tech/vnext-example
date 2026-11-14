using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// lp-policy-task (onEntry of explaining, runs before the explainer agent): the deterministic bank policy.
/// Amount 5,000–500,000 TRY, term 3–36 months, example installment at 3.5% a month at most 40% of income.
/// The model never decides eligibility; it only explains what this script decided. The template text written
/// to explain.text stays when the explainer agent fails.
/// </summary>
public class LpPolicyMapping : ScriptBase, IMapping
{
    private const double MonthlyRate = 0.035;
    private const double MinAmount = 5000, MaxAmount = 500000;
    private const int MinTerm = 3, MaxTerm = 36;
    private const double MaxInstallmentShare = 0.40;

    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(context.Instance?.Data));
        var amount = Num(root, "amount") ?? 0;
        var term = (int)Math.Round(Num(root, "termMonths") ?? 0);
        var income = Num(root, "monthlyIncome") ?? 0;
        var installment = term > 0 ? amount * MonthlyRate / (1 - Math.Pow(1 + MonthlyRate, -term)) : 0;
        var reason = amount < MinAmount || amount > MaxAmount ? "AMOUNT_OUT_OF_RANGE"
                   : term < MinTerm || term > MaxTerm ? "TERM_OUT_OF_RANGE"
                   : installment > income * MaxInstallmentShare ? "INSTALLMENT_OVER_40"
                   : "OK";
        return Task.FromResult(new ScriptResponse { Data = new Dictionary<string, object>
        {
            ["installment"] = Math.Round(installment, 2),
            ["eligibility"] = reason == "OK" ? "eligible" : "ineligible",
            ["policyReason"] = reason,
            ["explain"] = new Dictionary<string, object> { ["text"] = Template(reason), ["source"] = "template" }
        }});
    }

    private static double? Num(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in root.EnumerateObject())
        {
            if (!string.Equals(p.Name, "intake", StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var q in p.Value.EnumerateObject())
                if (string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase) && q.Value.ValueKind == JsonValueKind.Number)
                    return q.Value.GetDouble();
        }
        return null;
    }

    private static string Template(string reason) => reason switch
    {
        "OK" => "Ön değerlendirmeye göre talebiniz uygun görünüyor. Bu bir örnek hesaplamadır; kesin teklif değildir.",
        "AMOUNT_OUT_OF_RANGE" => "Talep edilen tutar 5.000–500.000 TL aralığının dışında olduğu için ön değerlendirme olumsuz. Bu bir örnek hesaplamadır.",
        "TERM_OUT_OF_RANGE" => "Talep edilen vade 3–36 ay aralığının dışında olduğu için ön değerlendirme olumsuz. Bu bir örnek hesaplamadır.",
        "INSTALLMENT_OVER_40" => "Örnek aylık taksit, aylık gelirinizin %40'ını aştığı için ön değerlendirme olumsuz. Daha düşük tutar ya da daha uzun vadeyle tekrar deneyebilirsiniz.",
        _ => "Ön değerlendirme tamamlandı. Bu bir örnek hesaplamadır; kesin teklif değildir."
    };
}
