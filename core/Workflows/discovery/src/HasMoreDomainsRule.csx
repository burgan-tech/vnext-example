using System.Threading.Tasks;
using System.Collections.Generic;
using BBT.Workflow.Scripting;

/// <summary>
/// Has More Domains Rule - Checks if there are more domains to process in current page
/// </summary>
public class HasMoreDomainsRule : IConditionMapping
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
                return false;

            // Check if there are more domains to process
            return currentDomainIndex < pendingDomains.Count;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

