using System.Threading.Tasks;
using System.Collections.Generic;
using BBT.Workflow.Scripting;

/// <summary>
/// No Domains Rule - Checks if no domains to process
/// </summary>
public class NoDomainsRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return true;

            var pendingDomains = context.Instance.Data.pendingDomains as List<object>;
            return pendingDomains == null || pendingDomains.Count == 0;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

