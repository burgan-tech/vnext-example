using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// More Approvals Pending Rule - Checks if more approvals are pending
/// </summary>
public class MoreApprovalsPendingRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            var approvedCount = (int)(context.Instance?.Data?.approvedCount ?? 0);
            var totalDocuments = (int)(context.Instance?.Data?.totalDocuments ?? 0);
            
            // More approvals pending if approved count is less than total
            return approvedCount < totalDocuments;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

