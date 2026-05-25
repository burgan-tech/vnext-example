using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

public class SetAccountTypeFromTransitionMapping : ScriptBase, ITransitionMapping
{
    public async Task<dynamic> Handler(ScriptContext context)
    {
        var data = context.Instance?.Data;
        dynamic result = new ExpandoObject();

        CopyIfExists(result, data, "initial");
        CopyIfExists(result, data, "userSession");
        CopyIfExists(result, data, "accountName");
        CopyIfExists(result, data, "currency");
        CopyIfExists(result, data, "branchCode");
        CopyIfExists(result, data, "initialDeposit");
        CopyIfExists(result, data, "accountPurpose");
        CopyIfExists(result, data, "notifications");
        CopyIfExists(result, data, "confirmed");
        CopyIfExists(result, data, "termsAccepted");
        CopyIfExists(result, data, "privacyPolicyAccepted");
        CopyIfExists(result, data, "policyValidation");
        CopyIfExists(result, data, "accountCreation");
        CopyIfExists(result, data, "termMonths");
        CopyIfExists(result, data, "maturityInstruction");
        CopyIfExists(result, data, "minimumDeposit");
        CopyIfExists(result, data, "riskProfile");
        CopyIfExists(result, data, "investmentExperience");
        CopyIfExists(result, data, "investmentObjective");
        CopyIfExists(result, data, "savingsGoalAmount");
        CopyIfExists(result, data, "targetDate");
        CopyIfExists(result, data, "autoTransferFromAccount");

        result.accountType = GetAccountType(context.Transition.Key);
        result.accountTypeSelectedAt = DateTime.UtcNow.ToString("o");

        return result;
    }

    private void CopyIfExists(dynamic target, dynamic source, string propertyName)
    {
        if (source == null || !HasProperty(source, propertyName))
        {
            return;
        }

        try
        {
            switch (propertyName)
            {
                case "initial": target.initial = source.initial; break;
                case "userSession": target.userSession = source.userSession; break;
                case "accountName": target.accountName = source.accountName; break;
                case "currency": target.currency = source.currency; break;
                case "branchCode": target.branchCode = source.branchCode; break;
                case "initialDeposit": target.initialDeposit = source.initialDeposit; break;
                case "accountPurpose": target.accountPurpose = source.accountPurpose; break;
                case "notifications": target.notifications = source.notifications; break;
                case "confirmed": target.confirmed = source.confirmed; break;
                case "termsAccepted": target.termsAccepted = source.termsAccepted; break;
                case "privacyPolicyAccepted": target.privacyPolicyAccepted = source.privacyPolicyAccepted; break;
                case "policyValidation": target.policyValidation = source.policyValidation; break;
                case "accountCreation": target.accountCreation = source.accountCreation; break;
                case "termMonths": target.termMonths = source.termMonths; break;
                case "maturityInstruction": target.maturityInstruction = source.maturityInstruction; break;
                case "minimumDeposit": target.minimumDeposit = source.minimumDeposit; break;
                case "riskProfile": target.riskProfile = source.riskProfile; break;
                case "investmentExperience": target.investmentExperience = source.investmentExperience; break;
                case "investmentObjective": target.investmentObjective = source.investmentObjective; break;
                case "savingsGoalAmount": target.savingsGoalAmount = source.savingsGoalAmount; break;
                case "targetDate": target.targetDate = source.targetDate; break;
                case "autoTransferFromAccount": target.autoTransferFromAccount = source.autoTransferFromAccount; break;
            }
        }
        catch
        {
            LogInformation($"SetAccountTypeFromTransitionMapping: skipped copying {propertyName}");
        }
    }

    private string GetAccountType(string? transitionKey)
    {
        return transitionKey switch
        {
            "select-time-deposit" => "time-deposit",
            "select-investment-account" => "investment-account",
            "select-savings-account" => "savings-account",
            _ => "demand-deposit"
        };
    }
}
