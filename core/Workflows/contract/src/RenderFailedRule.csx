using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Render Failed Rule - Checks if document render failed
/// </summary>
public class RenderFailedRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            var renderStatus = context.Instance?.Data?.renderStatus?.ToString();
            return renderStatus == "failed";
        }
        catch (Exception)
        {
            return true;
        }
    }
}

