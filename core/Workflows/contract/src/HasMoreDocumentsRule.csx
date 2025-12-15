using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Has More Documents Rule - Checks if there are more documents to process
/// </summary>
public class HasMoreDocumentsRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            var currentIndex = (int)(context.Instance?.Data?.currentDocumentIndex ?? 0);
            var totalDocuments = (int)(context.Instance?.Data?.totalDocuments ?? 0);
            
            // If current index is less than total, there are more documents
            return currentIndex < totalDocuments;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

