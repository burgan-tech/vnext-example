using System.Threading.Tasks;
using System.Collections.Generic;
using BBT.Workflow.Scripting;

/// <summary>
/// Page Complete Rule - Checks if current page processing is complete
/// </summary>
public class PageCompleteRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return false;

            var pendingDomains = context.Instance.Data.pendingDomains as List<object>;
            var currentDomainIndex = Convert.ToInt32(context.Instance.Data.currentDomainIndex ?? 0);
            
            if (pendingDomains == null)
                return true;

            return currentDomainIndex >= pendingDomains.Count;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

