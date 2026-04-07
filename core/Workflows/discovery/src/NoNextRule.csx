using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// No Next Rule - Checks if there is no next page (opposite of CheckPaginationRule)
/// </summary>
public class NoNextRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return true;

            var nextPageUrl = context.Instance.Data.nextPageUrl?.ToString();
            return string.IsNullOrEmpty(nextPageUrl);
        }
        catch (Exception)
        {
            return true;
        }
    }
}

