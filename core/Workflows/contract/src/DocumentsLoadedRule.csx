using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Documents Loaded Rule - Checks if documents are loaded successfully
/// </summary>
public class DocumentsLoadedRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            var totalDocuments = context.Instance?.Data?.totalDocuments ?? 0;
            return (int)totalDocuments > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

