using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// No Documents Rule - Checks if no documents found
/// </summary>
public class NoDocumentsRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            var totalDocuments = context.Instance?.Data?.totalDocuments ?? 0;
            return (int)totalDocuments == 0;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

