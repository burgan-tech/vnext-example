using System.Threading.Tasks;
using System.Collections.Generic;
using BBT.Workflow.Scripting;

/// <summary>
/// To Check Health Rule - Checks if should proceed to health check
/// </summary>
public class ToCheckHealthRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return false;

            var pendingDomains = context.Instance.Data.pendingDomains as List<object>;
            return pendingDomains != null && pendingDomains.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

