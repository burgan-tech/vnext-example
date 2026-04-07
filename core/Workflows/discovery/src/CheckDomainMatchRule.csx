using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class CheckDomainMatchRule : IConditionMapping
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

            // If either incoming value is missing/null, don't skip to completed
            if (string.IsNullOrEmpty(incomingHealthUrl) || string.IsNullOrEmpty(incomingAppId)
            || string.IsNullOrEmpty(incomingBaseUrl))
                return false;

            // If either existing value is missing/null, don't skip to completed
            if (string.IsNullOrEmpty(existingHealthUrl) || string.IsNullOrEmpty(existingAppId)
            ||string.IsNullOrEmpty(existingBaseUrl))
                return false;

            // If both values match, skip to completed
            if (incomingHealthUrl.Equals(existingHealthUrl, StringComparison.OrdinalIgnoreCase) &&
                incomingAppId.Equals(existingAppId, StringComparison.OrdinalIgnoreCase)&&
                incomingBaseUrl.Equals(existingBaseUrl, StringComparison.OrdinalIgnoreCase))
                return true; // Go to completed

            // Values don't match, go to update
            return false;
        }
        catch (Exception)
        {
            return false;  // On error, don't skip to completed
        }
    }
}



