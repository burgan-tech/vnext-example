using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class CheckDomainDontMatchRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            // Get incoming values from request
            var incomingHealthUrl = context.Instance.Data.healthUrl?.ToString();
            var incomingAppId = context.Instance.Data.appId?.ToString();
            var incomingBaseUrl = context.Instance.Data.baseUrl?.ToString();
            // Get existing values from domain check response
            var existingHealthUrl = context.Instance.Data.existingHealthUrl?.ToString();
            var existingAppId = context.Instance.Data.existingAppId?.ToString();
            var existingBaseUrl = context.Instance.Data.existingBaseUrl?.ToString();

            // If either incoming value is missing/null, proceed to update
            if (string.IsNullOrEmpty(incomingHealthUrl) || string.IsNullOrEmpty(incomingAppId)
            || string.IsNullOrEmpty(incomingBaseUrl))
                return true;

            // If either existing value is missing/null, proceed to update
            if (string.IsNullOrEmpty(existingHealthUrl) || string.IsNullOrEmpty(existingAppId)
            ||string.IsNullOrEmpty(existingBaseUrl))
                return true;

            // If either value differs, proceed to update
            if (incomingHealthUrl.Equals(existingHealthUrl, StringComparison.OrdinalIgnoreCase) &&
                incomingAppId.Equals(existingAppId, StringComparison.OrdinalIgnoreCase)&&
                incomingBaseUrl.Equals(existingBaseUrl, StringComparison.OrdinalIgnoreCase))
                return false; // Go to completed

            // Go to update
            return true; // Go to update state
        }
        catch (Exception)
        {
            return true;  // On error, proceed to update (conservative approach)
        }
    }
}



