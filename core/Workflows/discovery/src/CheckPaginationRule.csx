using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Check Pagination Rule - Checks if next link exists in response
/// </summary>
public class CheckPaginationRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context?.Instance?.Data == null)
                return false;

            var nextPageUrl = context.Instance.Data.nextPageUrl?.ToString();

            // Return true if next link is not null/empty
            return !string.IsNullOrEmpty(nextPageUrl);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

