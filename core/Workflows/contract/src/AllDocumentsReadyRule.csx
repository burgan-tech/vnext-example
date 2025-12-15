using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// All Documents Ready Rule - Checks if all documents are ready
/// </summary>
public class AllDocumentsReadyRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            var currentIndex = (int)(context.Instance?.Data?.currentDocumentIndex ?? 0);
            var totalDocuments = (int)(context.Instance?.Data?.totalDocuments ?? 0);
            
            // All documents are processed (started) when current index equals total
            return currentIndex >= totalDocuments;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

