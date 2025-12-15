using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// All Approved Rule - Checks if all documents are approved
/// </summary>
public class AllApprovedRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            var approvedCount = (int)(context.Instance?.Data?.approvedCount ?? 0);
            var totalDocuments = (int)(context.Instance?.Data?.totalDocuments ?? 0);
            
            // All approved when approved count equals total documents
            return approvedCount >= totalDocuments;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

