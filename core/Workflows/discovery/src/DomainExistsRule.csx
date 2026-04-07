using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Domain Exists Rule - Checks if domain already exists in workflow instance
/// Also checks ETag to determine if update is needed
/// Returns false (skip update) if ETags match, true (proceed to update) if ETags differ
/// </summary>
public class DomainExistsRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {

        try
        {
            if (context?.Instance?.Data == null)
                return false;  // If no data, assume domain doesn't exist

            var domainExists = context.Instance.Data.domainExists;

            if (domainExists == null)
                return false;  // If no check result, assume domain doesn't exist

            return domainExists == true;
        }
        catch (Exception)
        {
            return false;  // On error, assume domain doesn't exist
        }
        //     try
        //     {
        //         if (context?.Instance?.Data == null)
        //             return false;

        //         var domainExists = context.Instance.Data.domainExists;

        //         if (domainExists == null || domainExists != true)
        //             return false;

        //         // Get ETag values
        //         // instanceETag: from GetInstanceDataTask response (current instance ETag)
        //         // eTag: from transition call (sent ETag)
        //         var instanceETag = context.Instance.Data.instanceETag?.ToString();
        //         var sentETag = context.Instance.Data.eTag?.ToString();

        //         // If no ETags provided, proceed to update (conservative approach)
        //         if (string.IsNullOrEmpty(instanceETag) || string.IsNullOrEmpty(sentETag))
        //             return true;

        //         // If ETags match, skip update (return false - don't go to update state)
        //         if (instanceETag == sentETag)
        //             return false;

        //         // If ETags differ, proceed to update (return true - go to update state)
        //         return true;
        //     }
        //     catch (Exception)
        //     {
        //         // On error, proceed to update (conservative approach)
        //         return true;
        //     }
    }
}



